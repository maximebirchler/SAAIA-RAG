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
    [InlineData("llm_router")]
    [InlineData("llm_router_grounded_grid_row")]
    public async Task RouterGridInitialObservation_runs_before_speculative_semantic_reviews(
        string decisionSource)
    {
        var initialArguments = JsonSerializer.SerializeToElement(new
        {
            categoryPath = "Cuisine",
            kind = "navigation_entry",
            limit = 30,
            offset = 0
        });
        var intake = Intake("Construis une grille sourcee demandee.") with
        {
            CatalogHints = new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine", "Cuisine", 10, Array.Empty<string>())
            },
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    "router-plan-1",
                    "documents_navigation",
                    initialArguments,
                    decisionSource)
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "documents_navigation",
                    candidateScopePaths = new[] { "Cuisine" },
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm();
        var executor = new ScriptedToolExecutor(NavigationResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                MaximumSemanticCorrectionTurns = 0,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticColumnRoleReviewEnabled = true,
                SemanticCandidateStrategyEnabled = false,
                SemanticCandidateDefinitionEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.Empty(llm.ToolSets);
        Assert.Equal("documents.navigation", Assert.Single(executor.ToolNames));
        Assert.Equal(
            initialArguments.GetRawText(),
            Assert.Single(executor.Arguments).GetRawText());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.started"
            && trace.Fields["pre_observation_semantic_reviews_skipped"]
                == "true");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_candidate_definition.completed"
            && trace.Fields["attempted"] == "false");
    }

    [Fact]
    public async Task RouterGridInitialMission_allows_documentary_type_to_overlap_a_column()
    {
        var initialArguments = JsonSerializer.SerializeToElement(new
        {
            categoryPath = "Maintenance",
            limit = 40,
            offset = 0
        });
        var intake = Intake(
            "Dans Maintenance, prépare lundi à vendredi avec opération et preuve.")
            with
        {
            CatalogHints = new[]
                {
                    new SourceBackedCatalogHint(
                        "Maintenance",
                        "Maintenance",
                        10,
                        Array.Empty<string>())
                },
            InitialToolCalls = new[]
                {
                    new SourceBackedInitialToolCall(
                        "router-plan-1",
                        "documents_content_cards",
                        initialArguments,
                        "llm_router")
                },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                    JsonSerializer.SerializeToElement(new
                    {
                        planKind = "structured_layout",
                        deliverable = "grille de maintenance sourcee",
                        structuredLayout = true,
                        rowCount = 5,
                        columnCount = 2,
                        atomicEvidenceCount = 10,
                        atomicEvidenceType = "operation de maintenance",
                        atomicEvidenceTypeStatus =
                            "provisional_pre_observation",
                        initialCapability = "documents_content_cards",
                        candidateScopePaths = new[] { "Maintenance" },
                        rowHeader = "Jour",
                        rowLabels = new[]
                        {
                            "lundi", "mardi", "mercredi", "jeudi", "vendredi"
                        },
                        columns = new[] { "operation", "preuve" }
                    }),
                    "llm_router")
        };
        var llm = new ScriptedAgentLlm();
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Operation documentee", 1)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                MaximumSemanticCorrectionTurns = 0,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticColumnRoleReviewEnabled = true,
                SemanticCandidateStrategyEnabled = false,
                SemanticCandidateDefinitionEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        await runner.RunAsync(intake, CancellationToken.None);

        Assert.Empty(llm.ToolSets);
        Assert.Equal(
            "documents.content_cards",
            Assert.Single(executor.ToolNames));
        Assert.Equal(
            initialArguments.GetRawText(),
            Assert.Single(executor.Arguments).GetRawText());
    }

    [Fact]
    public async Task RouterGridMission_PreservesItsAtomicTargetForTheFirstAction()
    {
        var intake = Intake(
            "Construis une grille de Premiere a Deuxieme avec Option A et Option B.")
            with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                    JsonSerializer.SerializeToElement(new
                    {
                        planKind = "structured_layout",
                        deliverable = "grille sourcee",
                        structuredLayout = true,
                        rowCount = 2,
                        columnCount = 2,
                        atomicEvidenceCount = 4,
                        atomicEvidenceType = "instances documentees",
                        initialCapability = "",
                        rowHeader = "Periode",
                        rowLabels = new[] { "Premiere", "Deuxieme" },
                        columns = new[] { "Option A", "Option B" }
                    }),
                    "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            ColumnSemantics(
                ("Option A", "Premiere famille distincte d'instances documentees."),
                ("Option B", "Deuxieme famille distincte d'instances documentees.")),
            CandidateStrategy());
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 1)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                SemanticColumnRoleReviewEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            "submit_candidate_evidence_strategy",
            Assert.Single(llm.ToolSets[1]).Name);
        Assert.Equal(
            "documents.content_cards",
            Assert.Single(executor.ToolNames));
        Assert.Equal(
            JsonValueKind.Null,
            Assert.Single(executor.Arguments)
                .GetProperty("categoryPath")
                .ValueKind);
        Assert.Contains(
            "AXES_DU_LIVRABLE_A_NE_PAS_RECOPIER_COMME_OBJET_SOURCE: Periode | Premiere | Deuxieme | Option A | Option B",
            RequestText(llm, 1));
        Assert.Contains(
            "NOMBRE_MINIMUM: 4",
            RequestText(llm, 1));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_candidate_strategy.completed"
            && trace.Fields["attempted"] == "true"
            && trace.Fields["candidate_pool_relation"] == "shared_pool");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.initial_research_action.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["attempted"] == "false"
            && trace.Fields["decision_source"]
                == "llm_candidate_discovery"
            && trace.Fields["action_count"] == "1"
            && trace.Fields["tools"] == "documents_content_cards");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.router_plan.initial_actions_applied"
            && trace.Fields["decision_source"]
                == "llm_candidate_discovery");
    }

    [Fact]
    public async Task RouterGridMission_PreservesTheNarrowerLlmInitialScopeAndNavigationState()
    {
        var intake = Intake("Construis une grille sourcee demandee.") with
        {
            CatalogHints = new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine", "Cuisine", 10, Array.Empty<string>()),
                new SourceBackedCatalogHint(
                    "Catalogue commercial", "Catalogue commercial", 20,
                    Array.Empty<string>())
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(
                candidateScopeIds: new[] { 0 },
                candidatePoolRelation: "partitioned_pool"),
            Completion(Call(
                "initial-navigation",
                "submit_initial_research_batch",
                new
                {
                    sharedScope = "Cuisine",
                    actions = new[]
                    {
                        new
                        {
                            capability = "documents_navigation",
                            query = "",
                            scope = "Cuisine",
                            document = "",
                            anchor = "",
                            navigationKind = "navigation_entry",
                            limit = 12,
                            offset = 0
                        }
                    }
                })),
            Completion(Call("follow-up", "start_content_card_research", new
            {
                query = "",
                inventoryMode = "representative",
                limit = 4,
            })));
        var executor = new ScriptedToolExecutor(
            NavigationResult(),
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 7)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 2,
                SeparateActionAndWriter = false,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            new[] { "documents.navigation", "documents.content_cards" },
            executor.ToolNames);
        Assert.Equal("navigation_entry", executor.Arguments[0]
            .GetProperty("kind").GetString());
        Assert.Equal("Cuisine", executor.Arguments[1]
            .GetProperty("categoryPath").GetString());
        var followUpToolSetIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool =>
                tool.Name == "start_content_card_research"));
        Assert.True(followUpToolSetIndex >= 0);
        Assert.Contains(
            "PERIMETRES ACTIFS DECIDES PAR LE LLM: Cuisine",
            RequestText(llm, followUpToolSetIndex));
        Assert.Contains(
            "ANCRES DE NAVIGATION VISIBLES NON CITABLES",
            RequestText(llm, followUpToolSetIndex));
        Assert.Contains(
            "HydraulicPumpManual.pdf",
            RequestText(llm, followUpToolSetIndex));
        var followUpCards = Assert.Single(
            llm.ToolSets[followUpToolSetIndex],
            static tool => tool.Name == "start_content_card_research");
        Assert.False(followUpCards.Parameters
            .GetProperty("properties")
            .TryGetProperty("categoryPath", out _));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.research_transition.accepted"
            && trace.Fields["decision"] == "switch_to_content_cards"
            && trace.Fields["translated_tool"] == "documents_content_cards");
    }

    [Fact]
    public async Task ResearchTransition_LetsTheLlmPreserveTheDocumentOfVisibleEvidence()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "pompe HX-42 exigences",
            topK = 4
        });
        var intake = Intake("Résume les exigences documentées de la pompe HX-42.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "quatre exigences documentées",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "exigences documentées",
                    initialCapability = "rag_search",
                    rowHeader = "Groupe",
                    rowLabels = new[] { "A", "B" },
                    columns = new[] { "Exigence 1", "Exigence 2" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "audit-search",
                new[] { "E1" },
                Array.Empty<string>())),
            Completion(Call(
                "focused-navigation",
                "refine_document_navigation",
                new
                {
                    documentFocusEvidenceId = "E1",
                    query = "",
                    navigationKind = "all",
                    limit = 10
                })));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            NavigationResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 3,
                SeparateActionAndWriter = false,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateStrategyEnabled = false
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "documents.navigation" },
            executor.ToolNames);
        var transitionToolSetIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool =>
                tool.Name == "refine_document_navigation"));
        Assert.True(transitionToolSetIndex >= 0);
        Assert.Contains(
            "DOCUMENTS OBSERVES UTILISABLES POUR LA RECUPERATION",
            RequestText(llm, transitionToolSetIndex),
            StringComparison.Ordinal);
        Assert.Contains(
            "E1 | document=Manuals/HydraulicPumpManual.pdf",
            RequestText(llm, transitionToolSetIndex),
            StringComparison.Ordinal);
        var transitionTool = Assert.Single(
            llm.ToolSets[transitionToolSetIndex],
            static tool => tool.Name == "refine_document_navigation");
        var focusProperty = transitionTool.Parameters
            .GetProperty("properties")
            .GetProperty("documentFocusEvidenceId");
        Assert.Equal(
            new[] { "GLOBAL", "E1" },
            focusProperty
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());
        Assert.Contains(
            "documentFocusEvidenceId",
            transitionTool.Parameters
                .GetProperty("required")
                .EnumerateArray()
                .Select(static value => value.GetString()));
        var navigationArguments = executor.Arguments[1];
        Assert.Equal("doc-hx42", navigationArguments
            .GetProperty("docId").GetString());
        Assert.Equal("Manuals/HydraulicPumpManual.pdf", navigationArguments
            .GetProperty("docPath").GetString());
        Assert.Equal("HydraulicPumpManual.pdf", navigationArguments
            .GetProperty("docRef").GetString());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.research_transition.accepted"
            && trace.Fields["document_focus_evidence_id"] == "E1"
            && trace.Fields["document_focus_doc_id"] == "doc-hx42"
            && trace.Fields["document_focus_doc_path"]
                == "Manuals/HydraulicPumpManual.pdf");
    }

    [Fact]
    public async Task ResearchTransition_LetsTheLlmSearchInsideASelectedVisibleDocument()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "pompe HX-42 exigences",
            topK = 4
        });
        var intake = Intake("Résume les exigences documentées de la pompe HX-42.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "quatre exigences documentées",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "exigences documentées",
                    initialCapability = "rag_search",
                    rowHeader = "Groupe",
                    rowLabels = new[] { "A", "B" },
                    columns = new[] { "Exigence 1", "Exigence 2" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "audit-search",
                new[] { "E1" },
                Array.Empty<string>())),
            Completion(Call(
                "focused-search",
                "refine_focused_document_search",
                new
                {
                    documentFocusEvidenceId = "E1",
                    query = "protective devices operator training",
                    limit = 6
                })));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 3,
                SeparateActionAndWriter = false,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateStrategyEnabled = false
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(new[] { "rag.search", "rag.search" }, executor.ToolNames);
        var transitionToolSetIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool =>
                tool.Name == "refine_focused_document_search"));
        Assert.True(transitionToolSetIndex >= 0);
        var transitionTool = Assert.Single(
            llm.ToolSets[transitionToolSetIndex],
            static tool => tool.Name == "refine_focused_document_search");
        Assert.Contains(
            "corps du document",
            transitionTool.Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { "E1" },
            transitionTool.Parameters
                .GetProperty("properties")
                .GetProperty("documentFocusEvidenceId")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());

        var focusedArguments = executor.Arguments[1];
        Assert.Equal("protective devices operator training", focusedArguments
            .GetProperty("query").GetString());
        Assert.Equal(6, focusedArguments.GetProperty("topK").GetInt32());
        Assert.Equal("doc-hx42", focusedArguments
            .GetProperty("docId").GetString());
        Assert.Equal("Manuals/HydraulicPumpManual.pdf", focusedArguments
            .GetProperty("docPath").GetString());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.research_transition.accepted"
            && trace.Fields["decision"] == "search_focused_document"
            && trace.Fields["translated_tool"] == "rag_search"
            && trace.Fields["document_focus_evidence_id"] == "E1"
            && trace.Fields["document_focus_doc_id"] == "doc-hx42"
            && trace.Fields["document_focus_doc_path"]
                == "Manuals/HydraulicPumpManual.pdf");
    }

    [Fact]
    public async Task ResearchTransition_LetsTheLlmExpandContextAroundVisibleEvidence()
    {
        var (result, llm, executor) =
            await RunDocumentContextExpansionTransitionAsync("E1");

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "documents.context" },
            executor.ToolNames);
        var transitionToolSetIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool =>
                tool.Name == "expand_document_context"));
        Assert.True(transitionToolSetIndex >= 0);
        Assert.Contains(
            "passages complementaires",
            RequestText(llm, transitionToolSetIndex),
            StringComparison.OrdinalIgnoreCase);
        var transitionTool = Assert.Single(
            llm.ToolSets[transitionToolSetIndex],
            static tool => tool.Name == "expand_document_context");
        var focusProperty = transitionTool.Parameters
            .GetProperty("properties")
            .GetProperty("documentFocusEvidenceId");
        Assert.Equal(
            new[] { "E1" },
            focusProperty
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());
        Assert.Contains(
            "documentFocusEvidenceId",
            transitionTool.Parameters
                .GetProperty("required")
                .EnumerateArray()
                .Select(static value => value.GetString()));

        var contextArguments = executor.Arguments[1];
        Assert.Equal("doc-hx42", contextArguments
            .GetProperty("docId").GetString());
        Assert.Equal("Manuals/HydraulicPumpManual.pdf", contextArguments
            .GetProperty("docPath").GetString());
        Assert.Equal("Manuals/HydraulicPumpManual.pdf", contextArguments
            .GetProperty("docRef").GetString());
        Assert.Equal("doc-hx42:42:3", contextArguments
            .GetProperty("chunkId").GetString());
        Assert.Equal(42, contextArguments
            .GetProperty("pageStart").GetInt32());
        Assert.Equal(42, contextArguments
            .GetProperty("pageEnd").GetInt32());
        Assert.Equal(2, contextArguments
            .GetProperty("before").GetInt32());
        Assert.Equal(2, contextArguments
            .GetProperty("after").GetInt32());
        Assert.Equal(8, contextArguments
            .GetProperty("limit").GetInt32());
        Assert.Equal(0, contextArguments
            .GetProperty("offset").GetInt32());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.research_transition.accepted"
            && trace.Fields["decision"] == "expand_document_context"
            && trace.Fields["translated_tool"] == "documents_context"
            && trace.Fields["document_focus_evidence_id"] == "E1"
            && trace.Fields["document_focus_doc_id"] == "doc-hx42"
            && trace.Fields["document_focus_doc_path"]
                == "Manuals/HydraulicPumpManual.pdf");
    }

    [Fact]
    public async Task ResearchTransition_AllowsTheLlmToExplicitlyLeaveVisibleDocuments()
    {
        var (result, _, executor) =
            await RunDocumentFocusTransitionAsync("GLOBAL");

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "documents.navigation" },
            executor.ToolNames);
        var navigationArguments = executor.Arguments[1];
        Assert.True(
            !navigationArguments.TryGetProperty("docId", out var docId)
            || docId.ValueKind == JsonValueKind.Null);
        Assert.True(
            !navigationArguments.TryGetProperty("docPath", out var docPath)
            || docPath.ValueKind == JsonValueKind.Null);
        Assert.True(
            !navigationArguments.TryGetProperty("docRef", out var docRef)
            || docRef.ValueKind == JsonValueKind.Null);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.research_transition.accepted"
            && trace.Fields["document_focus_evidence_id"] == "GLOBAL"
            && string.IsNullOrEmpty(trace.Fields["document_focus_doc_id"])
            && string.IsNullOrEmpty(trace.Fields["document_focus_doc_path"]));
    }

    [Fact]
    public async Task ResearchTransition_RejectsAnUnpublishedDocumentFocusEvidenceId()
    {
        var (result, _, executor) =
            await RunDocumentFocusTransitionAsync("E999");

        Assert.False(result.IsSourceVerified);
        Assert.Equal(new[] { "rag.search" }, executor.ToolNames);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.rejected"
            && trace.Fields["tool"] == "refine_document_navigation"
            && trace.Fields["error"]
                == "research_transition_document_focus_invalid");
    }

    [Fact]
    public async Task RouterGridMission_ExecutesItsLlmChosenInitialBatchInParallel()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(candidatePoolRelation: "partitioned_pool"),
            Completion(Call(
                "initial-batch",
                "submit_initial_research_batch",
                new
                {
                    sharedScope = "Knowledge",
                    actions = new[]
                    {
                        new
                        {
                            capability = "documents_content_cards",
                            query = "",
                            scope = "Knowledge",
                            document = "",
                            anchor = "",
                            limit = 8,
                            offset = 0
                        },
                        new
                        {
                            capability = "documents_content_cards",
                            query = "",
                            scope = "Knowledge",
                            document = "",
                            anchor = "",
                            limit = 8,
                            offset = 8
                        }
                    }
                })));
        var executor = new ConcurrentToolExecutor();
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                SemanticCandidateAuditEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(2, executor.MaximumConcurrency);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.mechanical_adjustment"
            && trace.Fields.GetValueOrDefault("adjustment")
                == "pagination_offset_reset_to_new_route:8");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.initial_research_action.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["action_count"] == "2");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.parallel_batch.started"
            && trace.Fields["tool_calls"] == "2");
    }

    [Fact]
    public async Task RouterGridMission_RepairsAnIncompleteInitialActionProtocol()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(candidatePoolRelation: "partitioned_pool"),
            Completion(Call(
                "incomplete-action",
                "submit_initial_research_batch",
                new
                {
                    sharedScope = "Knowledge",
                    actions = new[]
                    {
                        new
                        {
                            capability = "documents_content_cards",
                            scope = "Knowledge",
                            limit = 4,
                            offset = 0
                        }
                    }
                })),
            Completion(Call(
                "repaired-action",
                "submit_initial_research_batch",
                new
                {
                    sharedScope = "Knowledge",
                    actions = new[]
                    {
                        new
                        {
                            capability = "documents_content_cards",
                            query = "",
                            scope = "Knowledge",
                            document = "",
                            anchor = "",
                            limit = 8,
                            offset = 0
                        }
                    }
                })));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 1)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Contains(
            "initial_research_batch_action_1:"
            + "fields_missing_or_invalid:query,document,anchor",
            RequestText(llm, 2));
        Assert.Equal(
            "documents.content_cards",
            Assert.Single(executor.ToolNames));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.initial_research_action.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["attempts"] == "2"
            && trace.Fields["decision_source"]
                == "llm_initial_research_batch");
    }

    [Fact]
    public async Task RouterGridMission_MechanicallyScalesAndCoalescesIdenticalInitialRoutes()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(candidatePoolRelation: "partitioned_pool"),
            Completion(Call(
                "over-budget-batch",
                "submit_initial_research_batch",
                new
                {
                    sharedScope = "Knowledge",
                    actions = new[]
                    {
                        new
                        {
                            capability = "documents_content_cards",
                            query = "",
                            scope = "Knowledge",
                            document = "",
                            anchor = "",
                            limit = 40,
                            offset = 0
                        },
                        new
                        {
                            capability = "documents_content_cards",
                            query = "",
                            scope = "Knowledge",
                            document = "",
                            anchor = "",
                            limit = 40,
                            offset = 0
                        }
                    }
                })));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 1)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 1,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal("documents.content_cards", Assert.Single(executor.ToolNames));
        Assert.Equal(
            40,
            Assert.Single(executor.Arguments)
                .GetProperty("limit")
                .GetInt32());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.initial_research_action.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["attempts"] == "1"
            && trace.Fields["failure_reason"]
                == "initial_research_batch_limits_scaled:80>40;"
                   + "initial_research_batch_duplicate_routes_coalesced:2>1");
    }

    [Fact]
    public async Task RouterGridMission_UsesTheRouterAtomicTargetForOneCompactInitialObservation()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            CatalogHints = new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine", "Cuisine", 10, Array.Empty<string>()),
                new SourceBackedCatalogHint(
                    "Catalogue commercial", "Catalogue commercial", 20,
                    Array.Empty<string>())
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    candidateScopePaths = new[] { "Cuisine" },
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(Completion(Call(
            "compact-initial-observation",
            "submit_initial_observation_decision",
            new
            {
                capability = "documents_navigation_entries",
                query = "",
                coverageBudget = "rejection_rate_unknown_or_high"
            })));
        var executor = new ScriptedToolExecutor(
            NavigationResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateStrategyEnabled = false
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            "Cuisine",
            Assert.Single(executor.Arguments)
                .GetProperty("categoryPath")
                .GetString());
        Assert.Equal(
            "navigation_entry",
            Assert.Single(executor.Arguments)
                .GetProperty("kind")
                .GetString());
        Assert.Equal(
            40,
            Assert.Single(executor.Arguments)
                .GetProperty("limit")
                .GetInt32());
        Assert.Single(llm.Requests);
        var initialResearchTool = Assert.Single(
            Assert.Single(llm.ToolSets),
            static tool =>
                string.Equals(
                    tool.Name,
                    "submit_initial_observation_decision",
                    StringComparison.Ordinal));
        Assert.False(initialResearchTool.Parameters
            .GetProperty("properties")
            .TryGetProperty("scope", out _));
        Assert.Contains(
            "PREUVES_ATOMIQUES: 4 instances documentees",
            RequestText(llm, 0));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_candidate_strategy.completed"
            && trace.Fields["attempted"] == "false"
            && trace.Fields["candidate_scope_paths"] == "Cuisine");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.initial_research_action.completed"
            && trace.Fields["decision_source"] == "llm_initial_observation"
            && trace.Fields["coverage_budget"]
                == "rejection_rate_unknown_or_high");
    }

    [Fact]
    public async Task CandidateDefinition_UsesFocusedLlmContractAndFlowsIntoAudit()
    {
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "candidate-definition",
                "submit_atomic_candidate_definition",
                new
                {
                    hypotheticalSinglePositionValue = "Tarte aux fruits",
                    candidateObjectType = "recette ou preparation culinaire nommee",
                    candidateEligibilityRule =
                        "Le libelle exact nomme une preparation culinaire autonome, pas un axe ni une rubrique."
                })));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());
        var intake = Intake(
            "Construis un planning du lundi au vendredi avec quatre repas par jour.") with
        {
            CatalogHints = new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine", "Cuisine", 10, Array.Empty<string>())
            }
        };
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "ReviewSemanticCandidateDefinitionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            runner,
            new object?[]
            {
                intake,
                "LIVRABLE: planning\nPREUVES_ATOMIQUES: 20 repas quotidien",
                "Jour",
                new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Petit-dejeuner"] = "Valeur adaptee au premier repas de la journee.",
                    ["Dejeuner"] = "Valeur adaptee au repas du milieu de journee.",
                    ["Collation"] = "Valeur adaptee a une prise alimentaire legere.",
                    ["Souper"] = "Valeur adaptee au repas du soir."
                },
                CancellationToken.None
            }));
        await task;
        var outcome = task.GetType().GetProperty("Result")!.GetValue(task)!;

        Assert.Single(llm.Requests);
        Assert.All(llm.ToolSets, Assert.Empty);
        Assert.Equal(
            new[] { "source_backed_atomic_candidate_definition_v7" },
            llm.StructuredOutputContracts.Select(static contract => contract.Name));
        Assert.Contains("hypotheticalSinglePositionValue", RequestText(llm, 0));
        Assert.Contains("PERIMETRES_DU_CATALOGUE", RequestText(llm, 0));
        Assert.Contains("Cuisine", RequestText(llm, 0));
        Assert.Contains("AXE_LIGNES", RequestText(llm, 0));
        Assert.Contains("ROLES_SEMANTIQUES_DES_COLONNES", RequestText(llm, 0));
        Assert.Contains(
            "Valeur adaptee a une prise alimentaire legere",
            RequestText(llm, 0));
        Assert.Contains("COUVERTURE DE TOUS LES ROLES", RequestText(llm, 0));
        Assert.Contains("LIGNE_DE_REFERENCE", RequestText(llm, 0));
        Assert.Contains("DESCRIPTION_DU_LIVRABLE", RequestText(llm, 0));
        Assert.Contains("Distingue explicitement l'unite de reponse", RequestText(llm, 0));
        Assert.Contains("une fiche ou une section consacree a UNE instance", RequestText(llm, 0));
        Assert.DoesNotContain("PREUVES_ATOMIQUES: 20 repas quotidien", RequestText(llm, 0));
        Assert.DoesNotContain(
            llm.ToolSets.SelectMany(static tools => tools),
            static tool => tool.Name is "documents_content_cards" or "rag_search");
        Assert.Equal(
            "recette ou preparation culinaire nommee",
            outcome.GetType().GetProperty("CandidateObjectType")!.GetValue(outcome));
        Assert.True((bool)outcome.GetType().GetProperty("ProtocolValid")!.GetValue(outcome)!);

        var directInitialMessagesMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildDirectInitialResearchActionMessages",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(directInitialMessagesMethod);
        var directInitialMessages = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentMessage>>(
            directInitialMessagesMethod!.Invoke(
                null,
                new object?[]
                {
                    intake,
                    "LIVRABLE: planning\nPREUVES_ATOMIQUES: 20 repas quotidien",
                    outcome,
                    new[] { "Cuisine" },
                    1,
                    string.Empty,
                    null
                }));
        var directInitialPrompt = string.Join(
            "\n",
            directInitialMessages.Select(static message => message.Content));
        Assert.Contains(
            "TYPE_D_OBJET_SOURCE_CIBLE_DECIDE_PAR_LE_LLM: "
            + "recette ou preparation culinaire nommee",
            directInitialPrompt);
        Assert.Contains(
            "CRITERE_D_ELIGIBILITE_DECIDE_PAR_LE_LLM: "
            + "Le libelle exact nomme une preparation culinaire autonome",
            directInitialPrompt);
        Assert.DoesNotContain(
            "PREUVES_ATOMIQUES: 20 repas quotidien",
            directInitialPrompt);

        var auditRuleMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildCandidateDefinitionAuditRule",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(auditRuleMethod);
        var auditRule = Assert.IsType<string>(auditRuleMethod.Invoke(
            null,
            new[] { outcome }));
        var auditMessagesMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildCandidateBatchAuditMessages",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(auditMessagesMethod);
        var auditMessages = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentMessage>>(auditMessagesMethod.Invoke(
            null,
            new object?[]
            {
                intake,
                "LIVRABLE: planning\nPREUVES_ATOMIQUES: 20 repas quotidien",
                "recette ou preparation culinaire nommee",
                auditRule,
                "Jour",
                new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
                new[] { "Petit-dejeuner", "Dejeuner", "Collation", "Souper" },
                new[]
                {
                    new EvidenceItem(
                        "E1",
                        "canonical_content_card",
                        "documents.content_cards",
                        string.Empty,
                        "doc-1",
                        "test.pdf",
                        "Cuisine/test.pdf",
                        null,
                        null,
                        1,
                        1,
                        "card-1",
                        "Recette complete de tarte aux fraises.",
                        "recette complete de tarte aux fraises",
                        null,
                        1,
                        "Cuisine",
                        "fr",
                        "fr",
                        "high",
                        null,
                        new Dictionary<string, string>
                        {
                            ["sourceAnchorLabel"] = "Tarte aux fraises",
                            ["kind"] = "recipe"
                        },
                        new Dictionary<string, string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>())
                },
                false
            }));
        var auditPrompt = string.Join(
            Environment.NewLine,
            auditMessages.Select(static message => message.Content));
        Assert.Contains("recette ou preparation culinaire nommee", auditPrompt);
        Assert.Contains("preparation culinaire autonome", auditPrompt);
    }

    [Fact]
    public async Task RouterGridMission_EnforcesTheCatalogScopeChosenByTheLlmStrategy()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            CatalogHints = new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine", "Cuisine", 10, Array.Empty<string>()),
                new SourceBackedCatalogHint(
                    "Catalogue commercial", "Catalogue commercial", 20,
                    Array.Empty<string>())
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(CandidateStrategy(
            candidateScopeIds: new[] { 1 }));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 1)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            "Cuisine",
            Assert.Single(executor.Arguments)
                .GetProperty("categoryPath")
                .GetString());
        Assert.Contains(
            "- 1: Cuisine",
            RequestText(llm, 0));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_candidate_strategy.completed"
            && trace.Fields["candidate_scope_paths"] == "Cuisine"
            && trace.Fields["initial_action_tool"]
                == "documents_content_cards");
    }

    [Fact]
    public async Task RouterGridMission_CorpusScopeAllowsTheLlmToChooseAnExactSubscope()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            CatalogHints = new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine", "Cuisine", 10, Array.Empty<string>()),
                new SourceBackedCatalogHint(
                    "Catalogue commercial", "Catalogue commercial", 20,
                    Array.Empty<string>())
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(CandidateStrategy(
            candidateScopeIds: new[] { 1 }));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 1)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            "Cuisine",
            Assert.Single(executor.Arguments)
                .GetProperty("categoryPath")
                .GetString());
        Assert.Contains("PERIMETRES_EXACTS:", RequestText(llm, 0));
        Assert.Contains("Cuisine", RequestText(llm, 0));
        var scopeSchema = Assert.Single(llm.ToolSets[0])
            .Parameters
            .GetProperty("properties")
            .GetProperty("candidateScopeIds")
            .GetProperty("items");
        Assert.Contains(
            scopeSchema.GetProperty("enum").EnumerateArray(),
            static item => item.GetInt32() == 0);
        Assert.Contains(
            scopeSchema.GetProperty("enum").EnumerateArray(),
            static item => item.GetInt32() == 1);
    }

    [Fact]
    public async Task RouterGridMission_RepairsSharedPoolWithASingleLlmChosenScope()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            CatalogHints = new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine", "Cuisine", 10, Array.Empty<string>()),
                new SourceBackedCatalogHint(
                    "Catalogue commercial", "Catalogue commercial", 20,
                    Array.Empty<string>())
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(
                candidateScopeIds: new[] { 1, 2 }),
            CandidateStrategy(
                candidateScopeIds: new[] { 1 }));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 1)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        var repairedScopeSchema = Assert.Single(llm.ToolSets[1])
            .Parameters
            .GetProperty("properties")
            .GetProperty("candidateScopeIds");
        Assert.Equal(3, repairedScopeSchema.GetProperty("maxItems").GetInt32());
        Assert.Contains(
            "candidate_strategy_scopes_invalid",
            RequestText(llm, 1));
        Assert.Equal(
            "Cuisine",
            Assert.Single(executor.Arguments)
                .GetProperty("categoryPath")
                .GetString());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_candidate_strategy.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["attempts"] == "2"
            && trace.Fields["candidate_scope_paths"] == "Cuisine");
    }

    [Fact]
    public async Task RouterGridMission_InheritsTheSourceObjectWithoutAskingTheDiscoveryStepToRedefineIt()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(CandidateStrategy());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 1)
            })),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        var strategyProperties = Assert.Single(llm.ToolSets[0])
            .Parameters
            .GetProperty("properties");
        Assert.False(strategyProperties.TryGetProperty(
            "singlePositionSourceObjectType",
            out _));
        Assert.True(strategyProperties.TryGetProperty(
            "candidateEligibilityRule",
            out _));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_candidate_strategy.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["attempts"] == "1"
            && trace.Fields["candidate_object_type"]
                == "instances documentees");
    }

    [Fact]
    public async Task RouterGridMission_AllowsLayoutTermsWithDocumentaryMeaningInCandidateStrategy()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(CandidateStrategy(
            candidateEligibilityRule:
                "Le libelle nomme une instance documentee applicable a une Periode.",
            sourceDiscoveryQuery: "instance par Periode"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 1)
            })),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_candidate_strategy.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["attempts"] == "1"
            && trace.Fields["source_discovery_query"]
                == "instance par Periode"
            && trace.Fields["candidate_eligibility_rule"]
                == "Le libelle nomme une instance documentee applicable a une Periode.");
    }

    [Fact]
    public async Task RouterGridMission_DefersCandidateEligibilityUntilRealEvidenceExists()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(CandidateStrategy());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 1)
            })),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_candidate_strategy.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["attempts"] == "1"
            && trace.Fields["candidate_eligibility_rule"]
                == "Le libelle exact nomme une instance autonome du type cible.");
    }

    [Fact]
    public async Task RouterGridMission_CandidateStrategyMaterializesOneDirectSharedPoolAction()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(CandidateStrategy());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 1)
            })),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_candidate_strategy.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["attempts"] == "1"
            && trace.Fields["candidate_object_type"]
                == "instances documentees"
            && trace.Fields["initial_action_tool"]
                == "documents_content_cards");
    }

    [Fact]
    public void CandidateCollection_NavigationTransitionCanContinueAnExactProductiveRoute()
    {
        const string question = "Construis une grille avec plusieurs options documentees.";
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ResolvableNavigationResult(),
            question);
        var executedArguments = JsonSerializer.SerializeToElement(new
        {
            categoryPath = "Knowledge",
            inventoryMode = "representative",
            limit = 20,
            offset = 0
        });
        var executedRequests = new[]
        {
            new RetrievalRequest(
                "documents.content_cards",
                string.Empty,
                "Knowledge",
                "test_productive_inventory",
                Limit: 20,
                Offset: 0,
                NextOffset: 20,
                MaterializedEvidenceCount: 20,
                NewEvidenceCount: 18,
                ToolArguments: executedArguments)
        };
        var resolvedNavigationEvidenceIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var buildMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildResearchTransitionTools",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(buildMethod);
        var transitionTools = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentToolDefinition>>(
                buildMethod!.Invoke(null, new object?[]
                {
                    bundle,
                    resolvedNavigationEvidenceIds,
                    executedRequests,
                    40
                }));
        var transitionTool = Assert.Single(
            transitionTools,
            static tool => tool.Name == "continue_document_pagination");
        Assert.Contains(
            "P1=documents_content_cards",
            transitionTool.Description,
            StringComparison.Ordinal);
        Assert.Contains(
            "rendement=18/20",
            transitionTool.Description,
            StringComparison.Ordinal);
        var contentCardTransition = Assert.Single(
            transitionTools,
            static tool => tool.Name == "start_content_card_research");
        Assert.Contains(
            "preuves deja acquises restent disponibles",
            contentCardTransition.Description,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Abandonne",
            contentCardTransition.Description,
            StringComparison.Ordinal);
        Assert.Contains(
            "continue:P1",
            transitionTool.Parameters
                .GetProperty("properties")
                .GetProperty("paginationRouteId")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => "continue:" + item.GetString()));

        var submittedCall = Call(
            "continue-productive-inventory",
            "continue_document_pagination",
            new
            {
                paginationRouteId = "P1"
            });
        var expandMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "TryExpandResearchTransitionCall",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(expandMethod);
        object?[] expansionArguments =
        {
            submittedCall,
            bundle,
            resolvedNavigationEvidenceIds,
            executedRequests,
            40,
            null,
            null,
            0,
            null
        };
        var expanded = Assert.IsType<bool>(
            expandMethod!.Invoke(null, expansionArguments));

        Assert.True(expanded);
        var expandedCall = Assert.IsType<SourceBackedAgentToolCall>(
            expansionArguments[5]);
        Assert.Equal("documents_content_cards", expandedCall.Name);
        Assert.Equal(
            "Knowledge",
            expandedCall.Arguments.GetProperty("categoryPath").GetString());
        Assert.Equal(
            "representative",
            expandedCall.Arguments.GetProperty("inventoryMode").GetString());
        Assert.Equal(20, expandedCall.Arguments.GetProperty("limit").GetInt32());
        Assert.Equal(20, expandedCall.Arguments.GetProperty("offset").GetInt32());
        Assert.Equal("continue_existing_pagination", expansionArguments[6]);
        Assert.Equal(2, expansionArguments[7]);
        Assert.Equal(string.Empty, expansionArguments[8]);
    }

    [Fact]
    public async Task CandidateCollection_ExposesNavigationBatchAndMechanicallyResolvesSelectedAnchors()
    {
        const string question = "Construis une grille avec deux options documentees.";
        var navigationResults = ResolvableNavigationResult();
        var navigationBundle = EvidenceBundleBuilder.FromToolResults(
            navigationResults,
            question);
        var navigationIds = navigationBundle.Items
            .Where(static item => item.SourceKind == "navigation_map")
            .Select(static item => item.EvidenceId)
            .ToArray();
        Assert.Equal(2, navigationIds.Length);

        var contextResults = NavigationBatchContextResults();
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(
                candidateObjectType: "option documentee",
                initialCapability: "documents_navigation_entries",
                initialLimit: 2),
            Completion(Call(
                "resolve-navigation-batch",
                "resolve_navigation_anchors",
                new
                {
                    evidenceIds = navigationIds
                })));
        var executor = new ScriptedToolExecutor(
            navigationResults,
            contextResults);
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 2,
                MaximumToolCalls = 2,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateAuditEnabled = true
            });
        var intake = Intake(question) with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille de deux options sourcees",
                    structuredLayout = true,
                    rowCount = 1,
                    columnCount = 2,
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "options",
                    initialCapability = "",
                    rowHeader = "Ligne",
                    rowLabels = new[] { "Unique" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            new[] { "documents.navigation", "documents.context_batch" },
            executor.ToolNames);
        var batchArguments = executor.Arguments[1];
        Assert.Equal(2, batchArguments.GetProperty("targets").GetArrayLength());
        var targets = batchArguments.GetProperty("targets")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(navigationIds, targets
            .Select(static target => target.GetProperty("evidenceId").GetString())
            .ToArray());
        Assert.Equal(
            new[] { "chunk-alpha", "chunk-beta" },
            targets
                .Select(static target => target.GetProperty("chunkId").GetString())
                .ToArray());
        Assert.Equal(
            new[] { "Option Alpha", "Option Beta" },
            targets
                .Select(static target => target
                    .GetProperty("sourceAnchorLabel")
                    .GetString())
                .ToArray());
        Assert.Equal(
            new[] { "anchor-alpha", "anchor-beta" },
            targets
                .Select(static target => target
                    .GetProperty("anchorId")
                    .GetString())
                .ToArray());
        Assert.All(targets, static target =>
        {
            Assert.Equal(0, target.GetProperty("before").GetInt32());
            Assert.Equal(1, target.GetProperty("after").GetInt32());
            Assert.Equal(3, target.GetProperty("limit").GetInt32());
        });
        var batchTool = Assert.Single(
            llm.ToolSets.SelectMany(static toolSet => toolSet),
            static tool => tool.Name == "resolve_navigation_anchors");
        Assert.Equal(
            navigationIds,
            batchTool.Parameters
                .GetProperty("properties")
                .GetProperty("evidenceIds")
                .GetProperty("items")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());
        Assert.Contains(result.EvidenceBundle.Items, static item =>
            item.SourceKind == "document_context"
            && item.ChunkId == "chunk-alpha"
            && item.PageStart == 11);
        Assert.Contains(result.EvidenceBundle.Items, static item =>
            item.SourceKind == "document_context"
            && item.ChunkId == "chunk-beta"
            && item.PageStart == 22);
    }

    [Fact]
    public async Task CandidateCollection_ResolvesAPageOnlyNavigationLocatorWithoutPromotingTheAnchor()
    {
        const string question = "Retrouve le passage localise dans le manuel.";
        var navigationResults = PageOnlyNavigationResult();
        var navigationBundle = EvidenceBundleBuilder.FromToolResults(
            navigationResults,
            question);
        var navigationAnchor = Assert.Single(
            navigationBundle.Items,
            static item => item.SourceKind == "navigation_map");
        Assert.Null(navigationAnchor.ChunkId);
        Assert.Equal(42, navigationAnchor.PageStart);
        Assert.Contains("orientation_only", navigationAnchor.RiskFlags);

        var llm = new ScriptedAgentLlm(
            CandidateStrategy(
                candidateObjectType: "passage documente",
                initialCapability: "documents_navigation_entries",
                initialLimit: 1),
            Completion(Call(
                "resolve-page-only-navigation",
                "resolve_navigation_anchors",
                new
                {
                    evidenceIds = new[] { navigationAnchor.EvidenceId }
                })));
        var executor = new ScriptedToolExecutor(
            navigationResults,
            PageOnlyNavigationContextResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 2,
                MaximumToolCalls = 2,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateAuditEnabled = true
            });
        var intake = Intake(question) with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille d'un passage source",
                    structuredLayout = true,
                    rowCount = 1,
                    columnCount = 1,
                    atomicEvidenceCount = 1,
                    atomicEvidenceType = "passage",
                    initialCapability = "",
                    rowHeader = "Ligne",
                    rowLabels = new[] { "Unique" },
                    columns = new[] { "Passage" }
                }),
                "llm_router")
        };

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            new[] { "documents.navigation", "documents.context_batch" },
            executor.ToolNames);
        var target = Assert.Single(
            executor.Arguments[1]
                .GetProperty("targets")
                .EnumerateArray());
        Assert.Equal(navigationAnchor.EvidenceId, target
            .GetProperty("evidenceId").GetString());
        Assert.Equal(JsonValueKind.Null, target.GetProperty("chunkId").ValueKind);
        Assert.Equal(42, target.GetProperty("pageStart").GetInt32());
        Assert.Equal(42, target.GetProperty("pageEnd").GetInt32());
        Assert.Contains(result.EvidenceBundle.Items, static item =>
            item.SourceKind == "navigation_map"
            && item.PageStart == 42
            && item.RiskFlags.Contains("orientation_only"));
        Assert.Contains(result.EvidenceBundle.Items, static item =>
            item.SourceKind == "document_context"
            && item.ChunkId == "doc-hx42:42:canonical"
            && item.PageStart == 42
            && item.Excerpt!.Contains("85 N", StringComparison.Ordinal));
    }

    [Fact]
    public void ResearchTransition_AcceptsAPageOnlyNavigationLocatorAsRetrievalFocus()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            PageOnlyNavigationResult(),
            "Retrouve le passage localise dans le manuel.");
        var navigationAnchor = Assert.Single(
            bundle.Items,
            static item => item.SourceKind == "navigation_map");
        var submittedCall = Call(
            "focused-page-only-search",
            "refine_focused_document_search",
            new
            {
                documentFocusEvidenceId = navigationAnchor.EvidenceId,
                query = "couple serrage",
                limit = 3
            });
        var expandMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "TryExpandResearchTransitionCall",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(expandMethod);
        object?[] expansionArguments =
        {
            submittedCall,
            bundle,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<RetrievalRequest>(),
            8,
            null,
            null,
            0,
            null
        };

        var expanded = Assert.IsType<bool>(
            expandMethod!.Invoke(null, expansionArguments));

        Assert.True(expanded);
        var expandedCall = Assert.IsType<SourceBackedAgentToolCall>(
            expansionArguments[5]);
        Assert.Equal("rag_search", expandedCall.Name);
        Assert.Equal("doc-hx42", expandedCall.Arguments
            .GetProperty("docId").GetString());
        Assert.Equal("Manuals/HydraulicPumpManual.pdf", expandedCall.Arguments
            .GetProperty("docPath").GetString());
        Assert.Equal("search_focused_document", expansionArguments[6]);
        Assert.Equal(string.Empty, expansionArguments[8]);
        Assert.Contains("orientation_only", navigationAnchor.RiskFlags);
    }

    [Fact]
    public async Task NativeLoop_RejectsAnUnobservedContextChunkBeforeTheExecutor()
    {
        const string question = "Retrouve le passage localise dans le manuel.";
        var navigationResults = PageOnlyNavigationResult();
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(
                candidateObjectType: "passage documente",
                initialCapability: "documents_navigation_entries",
                initialLimit: 1),
            Completion(Call(
                "invented-context-chunk",
                "documents_context",
                new
                {
                    docPath = "Manuals/HydraulicPumpManual.pdf",
                    chunkId = "chunk-1",
                    before = 0,
                    after = 2,
                    limit = 8
                })));
        var executor = new ScriptedToolExecutor(
            navigationResults,
            new ToolResults());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 2,
                MaximumToolCalls = 2,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateAuditEnabled = true
            });
        var intake = Intake(question) with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille d'un passage source",
                    structuredLayout = true,
                    rowCount = 1,
                    columnCount = 1,
                    atomicEvidenceCount = 1,
                    atomicEvidenceType = "passage",
                    initialCapability = "",
                    rowHeader = "Ligne",
                    rowLabels = new[] { "Unique" },
                    columns = new[] { "Passage" }
                }),
                "llm_router")
        };

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(new[] { "documents.navigation" }, executor.ToolNames);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.rejected"
            && trace.Fields["tool"] == "documents_context"
            && trace.Fields["error"]
                == "documents_context_chunk_id_not_observed");
    }

    [Fact]
    public void DocumentLocatorContract_RejectsAnObservedChunkMixedWithAnotherDocument()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            SearchResult(),
            "Retrouve la valeur sourcee.");
        var validateMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "ValidateObservedDocumentLocator",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(validateMethod);
        var coherentCall = Call(
            "coherent-context-locator",
            "documents_context",
            new
            {
                docId = "doc-hx42",
                docPath = "Manuals/HydraulicPumpManual.pdf",
                chunkId = "doc-hx42:42:3"
            });
        var mixedCall = Call(
            "mixed-context-locator",
            "documents_context",
            new
            {
                docId = "doc-other",
                docPath = "Manuals/OtherManual.pdf",
                chunkId = "doc-hx42:42:3"
            });

        var coherentError = validateMethod!.Invoke(
            null,
            new object[] { coherentCall, "documents.context", bundle });
        var mixedError = validateMethod.Invoke(
            null,
            new object[] { mixedCall, "documents.context", bundle });

        Assert.Null(coherentError);
        Assert.Equal(
            "documents_context_locator_identity_mismatch",
            Assert.IsType<string>(mixedError));
    }

    [Fact]
    public void TerminalAnswer_DoesNotExposeInternalProtocolNotes()
    {
        const string internalDecision =
            "Décision finale de l'orchestrateur LLM: submit_evidence_selection "
            + "evidenceIds: [\"9fb65de2-8e5f-df87-e960-bc54d5908308\"]";
        const string internalContract =
            "Le contrat mécanique exige 1 sources visibles distinctes, mais 0 seulement ont été observées.";
        var result = new SourceBackedPipelineResult(
            "terminal-public-contract",
            Intake("Retrouve le document demandé."),
            null,
            EvidenceBundle.Empty("Retrouve le document demandé."),
            new EvidenceJudgeDecision(
                "insufficient_evidence",
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<RetrievalRequest>(),
                new[] { internalDecision, internalContract }),
            null,
            null,
            null,
            false,
            Array.Empty<SourceBackedTraceEvent>());

        var answer = SourceBackedTerminalAnswer.Build(result);

        Assert.Contains("sources", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "submit_evidence_selection",
            answer,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "9fb65de2-8e5f-df87-e960-bc54d5908308",
            answer,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "contrat mécanique",
            answer,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EvidenceId", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(internalDecision, result.JudgeDecision.MissingEvidenceNotes[0]);
    }

    [Fact]
    public async Task SplitActionWriter_StopsAfterTwoIdenticalNoCitableDecisions()
    {
        const string question = "Retrouve le passage localise dans le manuel.";
        const string repeatedDecision =
            "submit_evidence_selection evidenceIds: [\"doc-hx42\"]";
        var scriptedCompletions = new List<SourceBackedAgentCompletion>
        {
            Completion(
                "LIVRABLE: une valeur sourcee\nDIMENSIONS: aucune matrice\n"
                + "PREUVES_ATOMIQUES: 1 valeur\n"
                + "MODE_PREUVES_ATOMIQUES: content_claim\n"
                + "POLITIQUE_SELECTION: single_item\n"
                + "INTENTIONS_RECHERCHE: couple serrage\n"
                + "APPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: une valeur directement soutenue\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune preuve exploitable"),
            Completion(Call(
                "navigation-only",
                "documents_navigation",
                new
                {
                    docPath = "Manuals/HydraulicPumpManual.pdf",
                    limit = 1,
                    offset = 0
                })),
            Completion(Call("zero-yield-1", "documents_content_cards", new
            {
                q = "target one",
                limit = 1,
                offset = 0
            })),
            Completion(Call("zero-yield-2", "documents_content_cards", new
            {
                q = "target two",
                limit = 1,
                offset = 0
            })),
            Completion(repeatedDecision),
            Completion(Call("zero-yield-3", "documents_content_cards", new
            {
                q = "target three",
                limit = 1,
                offset = 0
            })),
            Completion(repeatedDecision),
            Completion("Une troisième décision ne doit pas être consommée.")
        };
        scriptedCompletions.AddRange(Enumerable.Repeat(
            Completion(repeatedDecision),
            20));
        var llm = new ScriptedAgentLlm(scriptedCompletions.ToArray());
        var executor = new ScriptedToolExecutor(
            PageOnlyNavigationResult(),
            new ToolResults(),
            new ToolResults(),
            new ToolResults());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 10,
                MaximumToolCalls = 8,
                SeparateActionAndWriter = true,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateDefinitionEnabled = false,
                SemanticCandidateStrategyEnabled = false,
                SemanticCandidateAuditEnabled = false
            });

        var result = await runner.RunAsync(
            Intake(question),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(7, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.action.no_citable_evidence_repeat.stopped"
            && trace.Fields["identical_decisions"] == "2");
    }

    [Fact]
    public async Task CandidateCollection_PivotIgnoresRetainedNavigationIdsWithoutChangingLlmDecision()
    {
        const string question = "Construis une grille avec deux options documentees.";
        var navigationResults = ResolvableNavigationResult();
        var navigationIds = EvidenceBundleBuilder.FromToolResults(
                navigationResults,
                question)
            .Items
            .Where(static item => item.SourceKind == "navigation_map")
            .Select(static item => item.EvidenceId)
            .ToArray();
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(
                candidateObjectType: "option documentee",
                initialCapability: "documents_navigation_entries",
                initialLimit: 2),
            Completion(Call(
                "pivot-to-cards",
                "start_content_card_research",
                new
                {
                    query = "options documentees",
                    inventoryMode = "representative",
                    limit = 2
                })));
        var executor = new ScriptedToolExecutor(
            navigationResults,
            ContentCardInventoryResult(new[]
            {
                new MealCard("Option alpha", 11),
                new MealCard("Option beta", 22)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 2,
                MaximumToolCalls = 2,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateAuditEnabled = true
            });
        var intake = Intake(question) with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille de deux options sourcees",
                    structuredLayout = true,
                    rowCount = 1,
                    columnCount = 2,
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "options",
                    initialCapability = "",
                    rowHeader = "Ligne",
                    rowLabels = new[] { "Unique" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            new[] { "documents.navigation", "documents.content_cards" },
            executor.ToolNames);
        Assert.Equal(
            "options documentees",
            executor.Arguments[1].GetProperty("q").GetString());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.research_transition.accepted"
            && trace.Fields["decision"] == "switch_to_content_cards"
            && trace.Fields["ignored_evidence_ids"] == "2"
            && trace.Fields["translated_tool"] == "documents_content_cards");
    }

    [Fact]
    public async Task CandidateCollection_PivotWithEmptySelectionConsumesTheVisibleNavigationBatch()
    {
        const string question = "Construis une grille avec deux options documentees.";
        var navigationResults = ResolvableNavigationResult();
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(
                candidateObjectType: "option documentee",
                initialCapability: "documents_navigation_entries",
                initialLimit: 2),
            Completion(Call(
                "pivot-to-cards",
                "start_content_card_research",
                new
                {
                    query = "options documentees",
                    inventoryMode = "representative",
                    limit = 2
                })));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                navigationResults,
                ContentCardInventoryResult(new[]
                {
                    new MealCard("Option alpha", 11)
                })),
            Options() with
            {
                MaximumTurns = 2,
                MaximumToolCalls = 2,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateAuditEnabled = true
            });
        var intake = Intake(question) with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille de deux options sourcees",
                    structuredLayout = true,
                    rowCount = 1,
                    columnCount = 2,
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "options",
                    initialCapability = "",
                    rowHeader = "Ligne",
                    rowLabels = new[] { "Unique" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };

        var result = await runner.RunAsync(intake, CancellationToken.None);
        var transition = Assert.Single(
            result.TraceEvents,
            static trace => trace.EventName
                == "source_backed_agent_v2.research_transition.accepted");
        Assert.Equal("switch_to_content_cards", transition.Fields["decision"]);
        Assert.Equal("2", transition.Fields["ignored_evidence_ids"]);
    }

    [Fact]
    public async Task CandidateCollection_StopsOfferingSuccessfullyResolvedNavigationAnchors()
    {
        const string question = "Construis une grille avec deux options documentees.";
        var navigationResults = ResolvableNavigationResult();
        var navigationIds = EvidenceBundleBuilder.FromToolResults(
                navigationResults,
                question)
            .Items
            .Where(static item => item.SourceKind == "navigation_map")
            .Select(static item => item.EvidenceId)
            .ToArray();
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(
                candidateObjectType: "option documentee",
                initialCapability: "documents_navigation_entries",
                initialLimit: 2),
            Completion(Call(
                "resolve-navigation-batch",
                "resolve_navigation_anchors",
                new
                {
                    evidenceIds = navigationIds
                })),
            Completion(CandidateAuditCall(
                "reject-contexts",
                Array.Empty<string>(),
                new[] { "E3", "E4" })),
            Completion(Call(
                "continue-with-cards",
                "start_content_card_research",
                new
                {
                    query = "options documentees",
                    inventoryMode = "representative",
                    limit = 2
                })));
        var executor = new ScriptedToolExecutor(
            navigationResults,
            NavigationBatchContextResults(),
            ContentCardInventoryResult(new[]
            {
                new MealCard("Option gamma", 31),
                new MealCard("Option delta", 32)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 4,
                MaximumToolCalls = 3,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateAuditEnabled = true
            });
        var intake = Intake(question) with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille de deux options sourcees",
                    structuredLayout = true,
                    rowCount = 1,
                    columnCount = 2,
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "options",
                    initialCapability = "",
                    rowHeader = "Ligne",
                    rowLabels = new[] { "Unique" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            new[]
            {
                "documents.navigation",
                "documents.context_batch",
                "documents.content_cards"
            },
            executor.ToolNames);
        var contentCardsToolSet = llm.ToolSets.Last(static toolSet =>
            toolSet.Any(static tool =>
                tool.Name == "start_content_card_research"));
        Assert.DoesNotContain(
            contentCardsToolSet,
            static tool => tool.Name == "resolve_navigation_anchors");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.navigation_anchors.consumed"
            && trace.Fields["resolved_this_batch"] == "2"
            && trace.Fields["resolved_total"] == "2");
    }

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

    [Fact]
    public void EvidenceBundle_ContextAnchorPreservesSelectedNavigationIdentityOnlyOnAnchorChunk()
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.context",
            Result = JsonSerializer.SerializeToElement(new
            {
                requestedAnchorChunkId = "chunk-anchor",
                sourceAnchorChunkId = "chunk-anchor",
                sourceAnchorEvidenceId = "E7",
                sourceAnchorLabel = "Option Alpha",
                sourceAnchorId = "anchor-alpha",
                document = new
                {
                    docId = "doc-alpha",
                    docName = "catalogue.pdf",
                    docPath = "Knowledge/catalogue.pdf"
                },
                items = new object[]
                {
                    new
                    {
                        chunkId = "chunk-anchor",
                        pageStart = 11,
                        pageEnd = 11,
                        text = "INGREDIENTS : contenu exact de l'option alpha."
                    },
                    new
                    {
                        chunkId = "chunk-neighbor",
                        pageStart = 12,
                        pageEnd = 12,
                        text = "Passage voisin sans identite d'ancre."
                    }
                }
            })
        });

        var bundle = EvidenceBundleBuilder.FromToolResults(
            results,
            "Question generique");

        var anchor = Assert.Single(bundle.Items, static item =>
            item.ChunkId == "chunk-anchor");
        Assert.Equal(
            "Option Alpha",
            anchor.SelectionHints["sourceAnchorLabel"]);
        Assert.Equal(
            "E7",
            anchor.SelectionHints["sourceAnchorEvidenceId"]);
        Assert.Equal("anchor-alpha", anchor.AnchorId);
        Assert.Contains(
            "sourceAnchorEvidence:E7",
            anchor.Lineage);
        var neighbor = Assert.Single(bundle.Items, static item =>
            item.ChunkId == "chunk-neighbor");
        Assert.Null(neighbor.AnchorId);
        Assert.DoesNotContain(
            "sourceAnchorLabel",
            neighbor.SelectionHints.Keys);
    }

    [Fact]
    public void EvidenceBundle_ContextAnchorUsesOneMechanicalFallbackWhenBackendChunkIdentityDiffers()
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.context",
            Result = JsonSerializer.SerializeToElement(new
            {
                requestedAnchorChunkId = "navigation-chunk",
                sourceAnchorChunkId = "navigation-chunk",
                sourceAnchorEvidenceId = "E12",
                sourceAnchorLabel = "Option Beta",
                document = new
                {
                    docId = "doc-beta",
                    docName = "catalogue.pdf",
                    docPath = "Knowledge/catalogue.pdf"
                },
                items = new object[]
                {
                    new
                    {
                        chunkId = "resolved-chunk-a",
                        pageStart = 21,
                        text = "Premier passage retourne par le backend."
                    },
                    new
                    {
                        chunkId = "resolved-chunk-b",
                        pageStart = 22,
                        text = "Second passage retourne par le backend."
                    }
                }
            })
        });

        var bundle = EvidenceBundleBuilder.FromToolResults(
            results,
            "Question generique");

        var first = Assert.Single(bundle.Items, static item =>
            item.ChunkId == "resolved-chunk-a");
        Assert.Equal(
            "Option Beta",
            first.SelectionHints["sourceAnchorLabel"]);
        Assert.Equal(
            "first_context_fallback",
            first.SelectionHints["sourceAnchorResolution"]);
        var second = Assert.Single(bundle.Items, static item =>
            item.ChunkId == "resolved-chunk-b");
        Assert.DoesNotContain(
            "sourceAnchorLabel",
            second.SelectionHints.Keys);
    }

    [Fact]
    public async Task RouterGridMission_RejectsInitialContextWithoutAGroundedPointer()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(candidatePoolRelation: "partitioned_pool"),
            Completion(Call(
                "invalid-context-batch",
                "submit_initial_research_batch",
                new
                {
                    sharedScope = "",
                    actions = new[]
                    {
                        new
                        {
                            capability = "documents_context",
                            query = "",
                            scope = "",
                            document = "",
                            anchor = "repas lundi",
                            limit = 8,
                            offset = 0
                        }
                    }
                })),
            Completion(Call(
                "repaired-batch",
                "submit_initial_research_batch",
                new
                {
                    sharedScope = "Knowledge",
                    actions = new[]
                    {
                        new
                        {
                            capability = "documents_content_cards",
                            query = "",
                            scope = "Knowledge",
                            document = "",
                            anchor = "",
                            limit = 8,
                            offset = 0
                        }
                    }
                })));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 1)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            "documents.content_cards",
            Assert.Single(executor.ToolNames));
        Assert.Contains(
            "documents_context_requires_grounded_pointer",
            RequestText(llm, 2));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.initial_research_action.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["attempts"] == "2");
    }

    [Fact]
    public async Task RouterGridMission_KeepsValidInitialActionsWhenOneIsInvalid()
    {
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances documentees",
                    initialCapability = "",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(candidatePoolRelation: "partitioned_pool"),
            Completion(Call(
                "partially-valid-batch",
                "submit_initial_research_batch",
                new
                {
                    sharedScope = "Knowledge",
                    actions = new[]
                    {
                        new
                        {
                            capability = "documents_content_cards",
                            query = "",
                            scope = "Knowledge",
                            document = "",
                            anchor = "",
                            limit = 8,
                            offset = 0
                        },
                        new
                        {
                            capability = "documents_context",
                            query = "",
                            scope = "Knowledge",
                            document = "",
                            anchor = "",
                            limit = 8,
                            offset = 0
                        }
                    }
                })));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance documentee", 1)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(
            "documents.content_cards",
            Assert.Single(executor.ToolNames));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.initial_research_action.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["attempts"] == "1"
            && trace.Fields["action_count"] == "1"
            && trace.Fields["failure_reason"].StartsWith(
                "partial_invalid_actions:",
                StringComparison.Ordinal)
            && trace.Fields["failure_reason"].Contains(
                "documents_context_requires_grounded_pointer",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task StructuredPlanner_ExecutesItsFirstActionWithoutASecondPlanningTurn()
    {
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "plan-with-action",
                "submit_semantic_plan",
                new
                {
                    planKind = "single_item",
                    deliverable = "une option simple sourcee",
                    atomicEvidenceType = "option complete",
                    initialCapability = "rag_search",
                    initialQuery = "option simple",
                    initialScopeId = 0,
                    initialLimit = 8
                })),
            FastReview(
                "writer",
                "E1",
                "E1"),
            Completion("Je propose l'option simple documentee [E1]."),
            SemanticReview(
                "accept",
                "L'option est explicitement soutenue par la preuve selectionnee."));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                StructuredSemanticPlanningEnabled = true,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne une option simple."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { "rag.search" }, executor.ToolNames);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_plan.first_action_applied"
            && trace.Fields["decision_source"] == "semantic_planner");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_plan.completed"
            && trace.Fields["initial_action_decision_source"]
                == "semantic_planner"
            && trace.Fields["initial_action_tool"] == "rag_search"
            && trace.Fields["initial_action_arguments"]
                .Contains("option simple", StringComparison.Ordinal));
        var requiredPlannerFields = Assert.Single(llm.ToolSets[0])
            .Parameters
            .GetProperty("required")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .ToArray();
        Assert.Contains("planKind", requiredPlannerFields);
        Assert.DoesNotContain("rowLabels", requiredPlannerFields);
        Assert.DoesNotContain(
            "atomicEvidenceCount",
            requiredPlannerFields);
    }

    [Fact]
    public async Task StructuredPlanner_RepairsAnAtomicCountThatDoesNotMatchTheLlmDeclaredLayout()
    {
        var commonPlan = new
        {
            deliverable = "grille sourcee",
            structuredLayout = true,
            rowCount = 5,
            columnCount = 4,
            atomicEvidenceType = "instances completes distinctes",
            initialCapability = "documents_content_cards",
            rowHeader = "Ligne",
            rowLabels = new[] { "A", "B", "C", "D", "E" },
            columns = new[] { "Colonne 1", "Colonne 2", "Colonne 3", "Colonne 4" }
        };
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "plan-invalid",
                "submit_semantic_plan",
                new
                {
                    commonPlan.deliverable,
                    commonPlan.structuredLayout,
                    commonPlan.rowCount,
                    commonPlan.columnCount,
                    atomicEvidenceCount = 5,
                    commonPlan.atomicEvidenceType,
                    commonPlan.initialCapability,
                    commonPlan.rowHeader,
                    commonPlan.rowLabels,
                    commonPlan.columns
                })),
            Completion(Call(
                "plan-valid",
                "submit_semantic_plan",
                new
                {
                    commonPlan.deliverable,
                    commonPlan.structuredLayout,
                    commonPlan.rowCount,
                    commonPlan.columnCount,
                    atomicEvidenceCount = 20,
                    commonPlan.atomicEvidenceType,
                    commonPlan.initialCapability,
                    commonPlan.rowHeader,
                    commonPlan.rowLabels,
                    commonPlan.columns
                })),
            CandidateStrategy(
                candidateObjectType: "instances completes distinctes",
                initialLimit: 20),
            Completion("Je ne dispose encore d'aucune preuve citable."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with
            {
                MaximumTurns = 1,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis la grille sourcee demandee."),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal("submit_semantic_plan", Assert.Single(llm.ToolSets[0]).Name);
        Assert.Equal("submit_semantic_plan", Assert.Single(llm.ToolSets[1]).Name);
        Assert.True(llm.RequireToolCalls[0]);
        Assert.True(llm.RequireToolCalls[1]);
        Assert.Contains(
            "semantic_plan_atomic_count_not_layout_product",
            RequestText(llm, 1));
        Assert.Contains("DIMENSIONS: 5 x 4", RequestText(llm, 3));
        Assert.Contains("EN_TETE_LIGNES: Ligne", RequestText(llm, 3));
        Assert.Contains("PREUVES_ATOMIQUES: 20", RequestText(llm, 3));
        Assert.Contains("Colonne 4: Colonne 4", RequestText(llm, 3));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_plan.completed"
            && trace.Fields["structured_protocol_enabled"] == "true"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["planning_attempts"] == "2"
            && trace.Fields["required_atomic_evidence_count"] == "20"
            && trace.Fields["required_layout_rows"] == "5"
            && trace.Fields["required_layout_columns"] == "4");
    }

    [Fact]
    public async Task StructuredPlanner_CarriesItsExactAxesIntoSemanticRoleReviewAndSelection()
    {
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "plan",
                "submit_semantic_plan",
                new
                {
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "instances completes distinctes",
                    initialCapability = "documents_content_cards",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                })),
            ColumnSemantics(
                ("Option A", "Premiere famille distincte d'instances documentees."),
                ("Option B", "Deuxieme famille distincte d'instances documentees.")),
            CandidateStrategy(
                candidateObjectType: "instances completes distinctes"),
            Completion(Call("cards", "start_content_card_research", new
            {
                query = "",
                inventoryMode = "representative",
                limit = 4
            })),
            Completion(CandidateAuditCall(
                "audit",
                new[] { "E1", "E2", "E3", "E4" },
                Array.Empty<string>())),
             Completion(Call(
                 "selection",
                 "submit_evidence_selection",
                 new
                 {
                     evidenceIds = new[] { "E1", "E2", "E3", "E4" }
                 })),
            SemanticReview(
                "accept",
                "Les quatre instances sont distinctes et soutenues."),
            StructuredBatchReview("C01", "C02"),
            StructuredBatchReview("C03", "C04"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Instance A1", 1),
                new MealCard("Instance B1", 2),
                new MealCard("Instance A2", 3),
                new MealCard("Instance B2", 4)
            })),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                SemanticColumnRoleReviewEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis la grille sourcee demandee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal("submit_semantic_plan", Assert.Single(llm.ToolSets[0]).Name);
        Assert.Equal(
            "submit_candidate_evidence_strategy",
            Assert.Single(llm.ToolSets[2]).Name);
        Assert.Contains(
            llm.ToolSets.SelectMany(static tools => tools),
            static tool => tool.Name == "submit_column_semantics");
        Assert.DoesNotContain(
            llm.ToolSets.SelectMany(static tools => tools),
            static tool => tool.Name == "submit_axis_evidence_relations");
        Assert.Contains(
            llm.ToolSets[3],
            static tool => tool.Name == "start_content_card_research");
        Assert.DoesNotContain(
            llm.ToolSets,
            static toolSet => toolSet.Any(
                static tool => tool.Name == "manage_evidence_workspace"));
        var batchedAuditIndex = Enumerable.Range(0, llm.Requests.Count)
            .Where(index => RequestText(llm, index).Contains(
                "CANDIDATS A JUGER DANS CE LOT:",
                StringComparison.Ordinal))
            .Single();
        Assert.Empty(llm.ToolSets[batchedAuditIndex]);
        Assert.False(llm.RequireToolCalls[batchedAuditIndex]);
        Assert.Contains(llm.StructuredOutputContracts, static contract =>
            contract.Name == "source_backed_candidate_batch_audit_v5");
        Assert.Equal(0, llm.Temperatures[batchedAuditIndex]);
        Assert.Equal(128, llm.MaxTokens[batchedAuditIndex]);
        Assert.Contains(
            "TYPE ATOMIQUE ATTENDU",
            RequestText(llm, batchedAuditIndex));
        Assert.Contains(
            "titre_canonique",
            RequestText(llm, batchedAuditIndex));
        Assert.Contains(
            "Le backend a deja epingle mecaniquement le titre canonique exact",
            RequestText(llm, batchedAuditIndex));
        Assert.Contains(
            "instances completes distinctes",
            RequestText(llm, batchedAuditIndex));
        var selectionRequestIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool => tool.Name == "submit_evidence_selection"));
        Assert.True(selectionRequestIndex >= 0);
        var selectionToolSchema = Assert.Single(
            llm.ToolSets[selectionRequestIndex]).Parameters;
        var selectionProperties = selectionToolSchema.GetProperty("properties");
        Assert.False(selectionProperties.TryGetProperty("layout", out _));
        Assert.Equal(
            new[] { "evidenceIds" },
            selectionToolSchema.GetProperty("required")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());
        Assert.Contains("Periode", result.Answer);
        Assert.Contains("Option A", result.Answer);
        Assert.Contains("Premiere", result.Answer);
        Assert.Contains(
            "layout canonique deja decide",
            RequestText(llm, selectionRequestIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ORDRE CANONIQUE DECIDE PAR LE LLM",
            RequestText(llm, selectionRequestIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "- lignes, dans l'ordre: Premiere | Deuxieme",
            RequestText(llm, selectionRequestIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "- colonnes, dans l'ordre: Option A | Option B",
            RequestText(llm, selectionRequestIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Premiere | Deuxieme",
            RequestText(llm, 2));
        Assert.DoesNotContain(
            "CONTRAT D'AXES DECIDE PAR TON PLAN",
            RequestText(llm, 2));
        Assert.DoesNotContain(
            "instances completes distinctes",
            RequestText(llm, 2),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "hypothese de recherche",
            RequestText(llm, 2),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "planning de repas",
            RequestText(llm, 2),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_candidate_strategy.completed"
            && trace.Fields["candidate_object_type"]
                == "instances completes distinctes");
    }

    [Fact]
    public async Task StructuredPlanner_ProjectsEvidenceContractFromTheLlmCandidateStrategy()
    {
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "plan",
                "submit_semantic_plan",
                new
                {
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 1,
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "positions de grille",
                    initialCapability = "rag_search",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option" }
                })),
            CandidateStrategy(
                candidateObjectType: "positions de grille",
                initialLimit: 2),
            Completion("Je ne dispose encore d'aucune preuve citable."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with
            {
                MaximumTurns = 1,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis la grille sourcee demandee."),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Contains(
            "PREUVES_ATOMIQUES: 2 instances source distinctes, nommees et citables",
            RequestText(llm, 2));
        Assert.DoesNotContain(
            "PREUVES_ATOMIQUES: 2 positions de grille",
            RequestText(llm, 2));
        Assert.Contains(
            "mode de decouverte decide par le LLM=shared_pool",
            RequestText(llm, 2));
        Assert.Contains(
            "chaque instance doit etre jugee par le LLM",
            RequestText(llm, 2));
        Assert.Contains(
            "AXES_DU_LIVRABLE_A_NE_PAS_RECOPIER_COMME_OBJET_SOURCE: Periode | Premiere | Deuxieme | Option",
            RequestText(llm, 1));
        Assert.DoesNotContain(
            llm.ToolSets.SelectMany(static tools => tools),
            static tool => tool.Name == "submit_atomic_evidence_type");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.candidate_projected_evidence_contract.completed"
            && trace.Fields["applied"] == "true");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_candidate_strategy.completed"
            && trace.Fields["candidate_object_type"]
                == "positions de grille"
            && trace.Fields["candidate_eligibility_rule"]
                == "Le libelle exact nomme une instance autonome du type cible.");
    }

    [Fact]
    public async Task StructuredPlanner_LeavesRetrievalQuerySemanticsWithTheLlm()
    {
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "plan",
                "submit_semantic_plan",
                new
                {
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 1,
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "instances completes distinctes",
                    initialCapability = "rag_search",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option" }
                })),
            CandidateStrategy(
                candidateObjectType: "instances completes distinctes"),
            Completion(Call("polluted-search", "rag_search", new
            {
                query = "option Premiere",
                categoryPath = "Knowledge"
            })),
            Completion(CandidateAuditCall(
                "audit-polluted-search",
                Array.Empty<string>(),
                new[] { "E1" })),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 2
            })),
            Completion(CandidateAuditCall(
                "audit",
                new[] { "E2", "E3" },
                Array.Empty<string>())),
            Completion(Call(
                "selection",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" }
                })),
            SemanticReview(
                "accept",
                "Les deux instances sont distinctes et soutenues."));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance 1", 1),
                new MealCard("Instance 2", 2)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumWorkingEvidenceItems = 8,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis la grille sourcee demandee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "documents.content_cards" },
            executor.ToolNames);
        Assert.Equal(
            "option Premiere",
            executor.Arguments[0].GetProperty("query").GetString());
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.mechanical_adjustment"
            && trace.Fields.GetValueOrDefault("adjustment")?
                .Contains("placement_only", StringComparison.Ordinal) == true);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.candidate_projected_evidence_contract.completed"
            && trace.Fields["applied"] == "true");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["candidates"] == "1"
            && trace.Fields["approved"] == "0"
            && trace.Fields["rejected_evidence_ids"] == "E1");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.candidate_audit.performance"
            && trace.Fields["candidates"] == "1"
            && trace.Fields["executed_batches"] == "1"
            && trace.Fields["input_budget_splits"] == "0"
            && trace.Fields["largest_executed_batch_candidates"] == "1"
            && trace.Fields["llm_calls"] == "1"
            && trace.Fields["semantic_audit_llm_calls"] == "1");
    }

    [Fact]
    public async Task NativeLoop_LetsLlmNavigateSearchAndProduceMechanicallyVerifiedAnswer()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: reponse precise. Preuves: document, page et valeur technique."),
            Completion(Call("nav-1", "documents_navigation", new { docRef = "HydraulicPumpManual.pdf" })),
            Completion(Call("search-1", "rag_search", new
            {
                query = "couple serrage boulons couvercle HX-42",
                docId = "doc-hx42",
                pageStart = 42,
                pageEnd = 42
            })),
            Completion("Le couple de serrage du couvercle de la pompe HX-42 est de 85 N·m [E2]."),
            SemanticReview("accept", "La valeur est directement soutenue par E2."));
        var executor = new ScriptedToolExecutor(
            NavigationResult(),
            SearchResult());
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(Intake(
            "Selon HydraulicPumpManual.pdf, quel est le couple de serrage du couvercle de la pompe HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("85 N·m [E2]", result.Answer);
        Assert.Equal(new[] { "documents.navigation", "rag.search" }, executor.ToolNames);
        Assert.Single(result.CitedEvidence);
        Assert.Equal("HydraulicPumpManual.pdf", result.CitedEvidence[0].DocName);
        Assert.Equal(42, result.CitedEvidence[0].PageStart);
        Assert.Empty(llm.ToolSets[0]);
        Assert.Equal(0.2, llm.Temperatures[0]);
        Assert.Null(llm.Temperatures[1]);
        Assert.Contains(
            "PREUVES_ATOMIQUES indique le nombre",
            RequestText(llm, 0),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "ROLES_COLONNES",
            RequestText(llm, 0),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Pour la demande repas",
            RequestText(llm, 0),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("un corpus", RequestText(llm, 0), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "l'inventaire de cartes expose",
            RequestText(llm, 1),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "system", "user", "user" }, llm.Requests[2].Select(static message => message.Role));
        Assert.Contains("PLAN DE MISSION PRODUIT PAR LE LLM", RequestText(llm, 2));
        Assert.Contains("ANCRES DE NAVIGATION NON CITABLES", RequestText(llm, 2));
        Assert.Contains("HydraulicPumpManual.pdf", RequestText(llm, 2));
        Assert.DoesNotContain("E1", RequestText(llm, 2));
        Assert.Contains("E2 | groupe_source=", RequestText(llm, 3));
        Assert.Contains("85 N", RequestText(llm, 3));
    }

    [Fact]
    public async Task NativeLoop_PreservesFreshNavigationAnchorsAlongsideExistingCitableEvidence()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: reponse sourcee\nDIMENSIONS: fait\n"
                + "PREUVES_ATOMIQUES: 1 fait\nINTENTIONS_RECHERCHE: cible technique\n"
                + "APPROCHE_OUTILS: recherche puis navigation si utile\n"
                + "ACCEPTER_SI: fait cite\nINSUFFISANT_SEULEMENT_SI: aucune preuve"),
            Completion(Call("search-1", "rag_search", new
            {
                query = "couple serrage HX-42"
            })),
            Completion(Call(
                "nav-1",
                "documents_navigation",
                new { docRef = "HydraulicPumpManual.pdf" })),
            Completion("Le couple prescrit est de 85 NÂ·m [E1]."),
            SemanticReview("accept", "E1 soutient directement la valeur."));
        var executor = new ScriptedToolExecutor(SearchResult(), NavigationResult());
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Quel est le couple prescrit pour HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { "rag.search", "documents.navigation" }, executor.ToolNames);
        Assert.Contains("E1 | rag_hit", RequestText(llm, 3));
        Assert.Contains("ANCRES DE NAVIGATION NON CITABLES", RequestText(llm, 3));
        Assert.Contains("HydraulicPumpManual.pdf", RequestText(llm, 3));
    }

    [Fact]
    public async Task NativeLoop_PrioritizesTheLlmToolPreferenceAndDefersWorkspaceUntilEvidenceExists()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: options sourcees\nDIMENSIONS: options\n"
                + "PREUVES_ATOMIQUES: 1 option\nINTENTIONS_RECHERCHE: options\n"
                + "APPROCHE_OUTILS: documents_content_cards puis rag_search si necessaire\n"
                + "ACCEPTER_SI: option citee\nINSUFFISANT_SEULEMENT_SI: aucune option"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 10
            })),
            Completion("Une option documentee est disponible [E1]."),
            SemanticReview("accept", "E1 soutient directement l'option."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Option documentee", 7)
            })),
            Options());

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal("documents_content_cards", llm.ToolSets[1][0].Name);
        Assert.Equal(4, llm.ToolSets[1].Count);
        Assert.Contains(llm.ToolSets[1], static tool => tool.Name == "rag_search");
        Assert.DoesNotContain(llm.ToolSets[1], static tool =>
            tool.Name == "manage_evidence_workspace");
    }

    [Fact]
    public async Task NativeLoop_ExecutesTheFirstToolActionAuthoredByTheLlmPlan()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: option sourcee\nDIMENSIONS: option\n"
                + "PREUVES_ATOMIQUES: 1 option\nINTENTIONS_RECHERCHE: aucune avant inventaire\n"
                + "APPROCHE_OUTILS: documents_content_cards\n"
                + "PREMIERE_ACTION: documents_content_cards {\"categoryPath\":\"Cuisine\",\"limit\":20,\"offset\":0}\n"
                + "ACCEPTER_SI: option citee\nINSUFFISANT_SEULEMENT_SI: aucune option"),
            Completion("L'option Sticks de feta est documentee [E1]."),
            SemanticReview("accept", "E1 soutient directement l'option."));
        var executor = new ScriptedToolExecutor(ContentCardInventoryResult());
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { "documents.content_cards" }, executor.ToolNames);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_plan.first_action_applied"
            && trace.Fields["tool"] == "documents_content_cards");
    }

    [Fact]
    public async Task NativeLoop_ReusesTheLlmRouterActionWithoutAskingTheLlmToChooseItAgain()
    {
        var routerAction = Call("router-plan-1", "rag_search", new
        {
            query = "dessert chocolat facile",
            categoryPath = "Cuisine"
        });
        var intake = Intake("Je veux un dessert au chocolat facile.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    routerAction.Id,
                    routerAction.Name,
                    routerAction.Arguments,
                    "llm_router")
            }
        };
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: une proposition sourcee\nDIMENSIONS: proposition\n"
                + "PREUVES_ATOMIQUES: 1 proposition\n"
                + "INTENTIONS_RECHERCHE: dessert chocolat facile\n"
                + "APPROCHE_OUTILS: rag_search puis documents_context si necessaire\n"
                + "PREMIERE_ACTION: documents_content_cards "
                + "{\"categoryPath\":\"Cuisine\",\"limit\":20,\"offset\":0}\n"
                + "ACCEPTER_SI: proposition citee\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune proposition"),
            Completion("Le gateau au chocolat est documente [E1]."),
            SemanticReview("accept", "E1 soutient directement la proposition."));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { "rag.search" }, executor.ToolNames);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.router_plan.initial_actions_applied"
            && trace.Fields["decision_source"] == "llm_router");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_plan.first_action_applied");
    }

    [Fact]
    public async Task NativeLoop_ReusesTheLlmRouterSemanticMissionWithoutASecondPlannerCall()
    {
        var exactDeliverable =
            "Je veux une proposition documentaire précise, professionnelle et "
            + "directement exploitable, avec seulement les informations utiles, "
            + "une preuve canonique résoluble jusqu'au fichier et à la page, sans "
            + "répétition inutile, sans invention et en conservant exactement toutes "
            + "les exigences explicites de cette demande utilisateur assez longue.";
        Assert.True(exactDeliverable.Length > 220);
        var routerAction = Call("router-plan-1", "rag_search", new
        {
            query = "dessert chocolat facile",
            categoryPath = "Cuisine"
        });
        var intake = Intake("Je veux un dessert au chocolat facile.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    routerAction.Id,
                    routerAction.Name,
                    routerAction.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "single_item",
                    deliverable = exactDeliverable,
                    atomicEvidenceCount = 1,
                    atomicEvidenceType = "dessert complet et utilisable",
                    initialCapability = "",
                    questionFocus = "content",
                    requestedDocumentName = ""
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion("Le dessert au chocolat est documente [E1]."),
            SemanticReview("accept", "E1 soutient directement la proposition."));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { "rag.search" }, executor.ToolNames);
        Assert.Equal(2, llm.Requests.Count);
        Assert.DoesNotContain(llm.ToolSets, tools => tools.Any(tool =>
            tool.Name == "submit_semantic_plan"));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_plan.completed"
            && trace.Fields["planning_decision_source"] == "llm_router"
            && trace.Fields["planning_attempts"] == "0"
            && trace.Fields["prompt_tokens"] == "0");
    }

    [Fact]
    public async Task AdaptiveFastReview_NewWriterTextCannotInheritTheEvidenceSelectionApproval()
    {
        const string reviewReached = "Independent final-text review was reached.";
        var llm = new ContextOverflowOnceLlm(
            FastReview("writer", "E1", "E1"),
            Completion("La pression du module Atlas est de 999 unités fictives [E1]."),
            new InvalidOperationException(reviewReached));
        var runner = new SourceBackedAgentV2Runner(llm,
            new ScriptedToolExecutor(SearchResultAtPage(7,
                "La pression du module Atlas est de 73 unités fictives.")),
            Options() with { SeparateActionAndWriter = true, RequireEvidenceSelectionBeforeWriter = true });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            FastReviewIntake() with { UserQuestion = "Quelle pression le document indique-t-il pour Atlas ?" },
            CancellationToken.None));

        Assert.Equal(reviewReached, error.Message);
        Assert.Equal(3, llm.Requests.Count);
        var reviewContext = string.Join(Environment.NewLine, llm.Requests[2].Select(message => message.Content));
        Assert.Contains("999 unités fictives", reviewContext, StringComparison.Ordinal);
        Assert.Contains("73 unités fictives", reviewContext, StringComparison.Ordinal);
        Assert.Contains("BROUILLON A JUGER", reviewContext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdaptiveFastReview_HandsOneSufficientEvidenceDirectlyToWriter()
    {
        var intake = FastReviewIntake();
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            Completion("Je te propose le dessert au chocolat documente [E1]."),
            SemanticReview("accept", "E1 documente directement le dessert proposé."));
        var executor = new ScriptedToolExecutor(SearchResultAtPage(7,
            "Dessert au chocolat facile : une mousse au chocolat documentée."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { "rag.search" }, executor.ToolNames);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Empty(llm.ToolSets[0]);
        Assert.Empty(llm.ToolSets[1]);
        Assert.Equal(2, llm.Requests[1].Count);
        Assert.Contains(
            "PREUVES EXACTEMENT AUTORISEES",
            RequestText(llm, 1));
        Assert.DoesNotContain(
            "ETAT DE TRAVAIL COMPACT",
            RequestText(llm, 1));
        Assert.Contains(
            "une phrase de 30 mots",
            RequestText(llm, 1));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["selected_evidence_ids"] == "E1"
            && trace.Fields["decision"] == "ready"
            && trace.Fields["anchor_verified"] == "true"
            && trace.Fields["anchor_excerpt"].Contains(
                "Dessert au chocolat facile",
                StringComparison.Ordinal));
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.writer.inline_from_evidence_review");
    }

    [Fact]
    public async Task AdaptiveFastReview_CanSelectAndDraftInlineBeforeIndependentReview()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Suivez la méthode documentée pour le couvercle."),
            SemanticReview(
                "accept",
                "La réponse couvre la demande et reste fidèle à la preuve citée."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(
            "Suivez la méthode documentée pour le couvercle [E1].",
            result.Answer);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.writer.inline_from_evidence_review"
            && trace.Fields["shared_llm_call"] == "true"
            && trace.Fields["selected_evidence_ids"] == "E1");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task AdaptiveFastReview_InlineAnswerRequiresIndependentSemanticReview()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Le manuel indique une information technique."),
            SemanticReview(
                "accept",
                "Le brouillon répond à la demande et reprend fidèlement la preuve citée."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(
                4,
                "Le manuel exige de verrouiller le capot avant toute maintenance.")),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion =
                    "Quelle information de sécurité ce manuel documente-t-il ?",
                QuestionFocus = "document_family_or_type"
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task AdaptiveFastReview_InlineRevisionCommitsAfterSourceVerificationWithoutSecondReview()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Manuel technique."),
            SemanticReview(
                "revise",
                "Le libellé est trop générique ; formule l'information de sécurité visible."),
            Completion(
                "Le manuel exige de verrouiller le capot avant toute maintenance [E1]."));
        var executor = new ScriptedToolExecutor(SearchResultAtPage(
            4,
            "Le manuel exige de verrouiller le capot avant toute maintenance."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion =
                    "Quelle information de sécurité ce manuel documente-t-il ?",
                QuestionFocus = "document_family_or_type"
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            "Le manuel exige de verrouiller le capot avant toute maintenance [E1].",
            result.Answer);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Single(executor.ToolNames);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.inline_answer_revision.returned_to_writer"
            && trace.Fields["selected_evidence"] == "E1");
        Assert.Equal(
            new[] { "revise" },
            result.TraceEvents
                .Where(static trace =>
                    trace.EventName
                        == "source_backed_agent_v2.semantic_review.completed")
                .Select(static trace => trace.Fields["decision"])
                .ToArray());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.review_directed_revision.committed_after_source_verification"
            && trace.Fields["selected_evidence"] == "E1");
    }

    [Fact]
    public async Task AdaptiveFastReview_InvalidReviewDirectedRevisionStillRequiresRepairAndReview()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Manuel technique."),
            SemanticReview(
                "revise",
                "Formule l'information de sécurité visible."),
            Completion(
                "Le manuel exige de verrouiller le capot avant toute maintenance."),
            Completion(
                "Le manuel exige de verrouiller le capot avant toute maintenance [E1]."),
            SemanticReview(
                "accept",
                "La réponse réparée est exacte et correctement citée."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(
                4,
                "Le manuel exige de verrouiller le capot avant toute maintenance.")),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion =
                    "Quelle information de sécurité ce manuel documente-t-il ?",
                QuestionFocus = "document_family_or_type"
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(5, llm.Requests.Count);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.review_directed_revision.committed_after_source_verification");
        Assert.Equal(
            new[] { "revise", "accept" },
            result.TraceEvents
                .Where(static trace =>
                    trace.EventName
                        == "source_backed_agent_v2.semantic_review.completed")
                .Select(static trace => trace.Fields["decision"])
                .ToArray());
    }

    [Fact]
    public async Task SemanticJudge_At4096ReceivesCompleteCitedEvidenceWhenMeasuredPromptFits()
    {
        const string criticalTail =
            "CRITICAL TAIL FACT: calibrated pressure equals 73 fictional units.";
        var longEvidence =
            "Technical section opening. "
            + new string('x', 260)
            + " "
            + criticalTail;
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "The calibrated pressure equals 73 fictional units."),
            SemanticReview(
                "accept",
                "E1 explicitly supports the calibrated pressure stated in the draft."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(7, longEvidence)),
            Options() with
            {
                MaximumContextTokens = 4096,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion =
                    "What calibrated pressure is explicitly documented?"
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(
            criticalTail,
            RequestText(llm, 1),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticJudge_CompactsScaffoldingButPreservesCompleteCitedEvidence()
    {
        const string criticalTail =
            "CRITICAL TAIL FACT: calibrated pressure equals 73 fictional units.";
        var longEvidence =
            "Technical section opening. "
            + new string('x', 260)
            + " "
            + criticalTail;
        var llm = new TokenCountingScriptedAgentLlm(
            new[] { 3600, 2000 },
            FastReview(
                "answer",
                "E1",
                "E1",
                "The calibrated pressure equals 73 fictional units."),
            SemanticReview(
                "accept",
                "The compact request still contains sufficient cited evidence."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(7, longEvidence)),
            Options() with
            {
                MaximumContextTokens = 4096,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion =
                    "What calibrated pressure is explicitly documented?"
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { 3600, 2000 }, llm.InputTokenCountsReturned);
        Assert.Equal(2, llm.CountedRequests.Count);
        Assert.Contains(
            criticalTail,
            string.Join(
                Environment.NewLine,
                llm.CountedRequests[0].Select(static message => message.Content)),
            StringComparison.Ordinal);
        Assert.Contains(
            criticalTail,
            string.Join(
                Environment.NewLine,
                llm.CountedRequests[1].Select(static message => message.Content)),
            StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["context_recovery_used"] == "true"
            && trace.Fields["evidence_context_mode"] == "measured_compact_fallback"
            && trace.Fields["exact_input_tokens"] == "2000");
    }

    [Fact]
    public async Task SemanticJudge_RefusesWhenCompleteEvidenceStillExceedsMeasuredContext()
    {
        var llm = new TokenCountingScriptedAgentLlm(
            new[] { 4300, 4200 },
            FastReview("answer", "E1", "E1", "The calibrated pressure equals 73 fictional units."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(7,
                "Technical section. " + new string('x', 260) + " Calibrated pressure equals 73 fictional units.")),
            Options() with
            {
                MaximumContextTokens = 4096,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            FastReviewIntake() with { UserQuestion = "What calibrated pressure is explicitly documented?" },
            CancellationToken.None));

        Assert.Contains("outside the measured", error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { 4300, 4200 }, llm.InputTokenCountsReturned);
        Assert.Equal(1, llm.CompleteCallCount);
    }

    [Fact]
    public async Task SemanticJudge_HttpContextRecoveryPreservesCompleteCitedEvidence()
    {
        const string criticalTail = "CRITICAL TAIL FACT: calibrated pressure equals 73 fictional units.";
        var llm = new ContextOverflowOnceLlm(
            FastReview("answer", "E1", "E1", "The calibrated pressure equals 73 fictional units."),
            new HttpRequestException("request exceeds the available context size", null,
                System.Net.HttpStatusCode.BadRequest),
            SemanticReview("accept", "The full evidence supports the pressure."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(7, "Technical section. " + new string('x', 260) + criticalTail)),
            Options() with
            {
                MaximumContextTokens = 4096,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with { UserQuestion = "What calibrated pressure is explicitly documented?" },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Contains(criticalTail, string.Join(Environment.NewLine,
            llm.Requests[2].Select(static message => message.Content)), StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["evidence_context_mode"] == "http_context_emergency_fallback");
    }

    [Fact]
    public async Task AdaptiveFastReview_NamedItemWindowRevisionReturnsSameSelectionDirectlyOnce()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReview(
                "accept",
                "E1",
                "",
                ("E1", "match"),
                ("E3", "nonmatch")),
            Completion(
                "The selected manual documents the sibling safety detail [E2]."),
            SemanticReview(
                "revise",
                "Keep the same cited proof and state the safety detail more precisely."),
            Completion(
                "The selected manual requires locking the cover before service [E2]."),
            SemanticReview(
                "accept",
                "E2 directly supports the revised safety statement."));
        var executor = new ScriptedToolExecutor(
            FastReviewDuplicateSourceResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(6, llm.Requests.Count);
        Assert.Single(executor.ToolNames);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.flat_selection_revision.returned_to_writer"
            && trace.Fields["selected_evidence"] == "E1");
        Assert.Equal(
            new[] { "revise", "accept" },
            result.TraceEvents
                .Where(static trace =>
                    trace.EventName
                        == "source_backed_agent_v2.semantic_review.completed")
                .Select(static trace => trace.Fields["decision"])
                .ToArray());
    }

    [Fact]
    public async Task AdaptiveFastReview_InlineRevisionUsesPreferredObservedAlternativeAndIsReviewedAgain()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Une option provisoire."),
            SemanticReview(
                "revise",
                "E1 est trop partiel ; E3 est l'alternative complete deja visible.",
                rejectedEvidenceIds: new[] { "E1" },
                preferredAlternativeEvidenceIds: new[] { "E3" }),
            Completion(
                "L'option complete est documentee avec ses elements utiles [E3]."),
            SemanticReview(
                "accept",
                "La revision utilise l'alternative choisie et repond a la demande."));
        var executor = new ScriptedToolExecutor(FastReviewSingleSourceAlternativesResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion = "Donne l'option complete deja documentee."
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            "L'option complete est documentee avec ses elements utiles [E3].",
            result.Answer);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Single(executor.ToolNames);
        Assert.Empty(llm.ToolSets[2]);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.inline_answer_alternative_revision.returned_to_writer"
            && trace.Fields["rejected_evidence"] == "E1"
            && trace.Fields["selected_evidence"] == "E3");
        Assert.Equal(
            new[] { "revise", "accept" },
            result.TraceEvents
                .Where(static trace =>
                    trace.EventName
                        == "source_backed_agent_v2.semantic_review.completed")
                .Select(static trace => trace.Fields["decision"])
                .ToArray());
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task AdaptiveFastReview_InlineAlternativeRevisionChainsDistinctPreferredEvidenceAndReviewsEachDraft()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Une option initiale."),
            SemanticReview(
                "revise",
                "E1 est trop partiel ; E3 est une alternative observee.",
                rejectedEvidenceIds: new[] { "E1" },
                preferredAlternativeEvidenceIds: new[] { "E3" }),
            Completion("La premiere revision documentee utilise l'alternative choisie [E3]."),
            SemanticReview(
                "revise",
                "E3 reste insuffisant ; E5 est une autre alternative observee.",
                rejectedEvidenceIds: new[] { "E3" },
                preferredAlternativeEvidenceIds: new[] { "E5" }),
            Completion("La revision finale documentee utilise l'alternative choisie [E5]."),
            SemanticReview(
                "accept",
                "La derniere revision repond a la demande avec la preuve choisie."));
        var executor = new ScriptedToolExecutor(
            FastReviewSingleSourceAlternativesResult(5));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                MaximumSemanticCorrectionTurns = 2
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion = "Donne l'option finale deja documentee."
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            "La revision finale documentee utilise l'alternative choisie [E5].",
            result.Answer);
        Assert.Equal(6, llm.Requests.Count);
        Assert.Single(executor.ToolNames);
        Assert.Empty(llm.ToolSets[2]);
        Assert.Empty(llm.ToolSets[4]);
        var transitions = result.TraceEvents
            .Where(static trace =>
                trace.EventName
                    == "source_backed_agent_v2.inline_answer_alternative_revision.returned_to_writer")
            .ToArray();
        Assert.Collection(
            transitions,
            first =>
            {
                Assert.Equal("E1", first.Fields["rejected_evidence"]);
                Assert.Equal("E3", first.Fields["selected_evidence"]);
                Assert.Equal("1", first.Fields["revision_attempt"]);
                Assert.Equal("2", first.Fields["maximum_revision_attempts"]);
            },
            second =>
            {
                Assert.Equal("E3", second.Fields["rejected_evidence"]);
                Assert.Equal("E5", second.Fields["selected_evidence"]);
                Assert.Equal("2", second.Fields["revision_attempt"]);
                Assert.Equal("2", second.Fields["maximum_revision_attempts"]);
            });
        Assert.Equal(
            new[] { "revise", "revise", "accept" },
            result.TraceEvents
                .Where(static trace =>
                    trace.EventName
                        == "source_backed_agent_v2.semantic_review.completed")
                .Select(static trace => trace.Fields["decision"])
                .ToArray());
        Assert.Equal(
            3,
            result.TraceEvents.Count(static trace =>
                trace.EventName == "source_backed_agent_v2.answer.verified"
                && trace.Fields["valid"] == "true"));
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task AdaptiveFastReview_InlineAlternativeRevisionStopsAtConfiguredSemanticCorrectionBudget()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Une option initiale."),
            SemanticReview(
                "revise",
                "E1 est trop partiel ; E3 est une alternative observee.",
                rejectedEvidenceIds: new[] { "E1" },
                preferredAlternativeEvidenceIds: new[] { "E3" }),
            Completion("La revision documentee utilise l'alternative choisie [E3]."),
            SemanticReview(
                "revise",
                "E3 reste insuffisant ; E5 est une autre alternative observee.",
                rejectedEvidenceIds: new[] { "E3" },
                preferredAlternativeEvidenceIds: new[] { "E5" }));
        var executor = new ScriptedToolExecutor(
            FastReviewSingleSourceAlternativesResult(5));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                MaximumTurns = 1,
                MaximumSemanticCorrectionTurns = 1
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion = "Donne l'option finale deja documentee."
            },
            CancellationToken.None);

        Assert.Equal(4, llm.Requests.Count);
        Assert.Single(executor.ToolNames);
        var transition = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.inline_answer_alternative_revision.returned_to_writer");
        Assert.Equal("E3", transition.Fields["selected_evidence"]);
        Assert.Equal("1", transition.Fields["revision_attempt"]);
        Assert.Equal("1", transition.Fields["maximum_revision_attempts"]);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.inline_answer_alternative_revision.returned_to_writer"
            && trace.Fields["selected_evidence"] == "E5");
    }

    [Fact]
    public void InlineFastAnswerRevisionSignatureIsCanonicalAcrossOrderCaseAndDuplicates()
    {
        Assert.Equal(
            "E3,E5",
            SourceBackedAgentV2Runner.BuildInlineFastAnswerRevisionSignature(
                new[] { "e5", " E3 ", "E5", "", "  " }));
        Assert.Equal(
            SourceBackedAgentV2Runner.BuildInlineFastAnswerRevisionSignature(
                new[] { "E3", "E5" }),
            SourceBackedAgentV2Runner.BuildInlineFastAnswerRevisionSignature(
                new[] { "e5", "e3" }));
    }

    [Fact]
    public async Task AdaptiveFastReview_CanClarifyWhenSeveralObservedCandidatesFit()
    {
        const string question =
            "Souhaitez-vous la variante classique ou la variante au chocolat ?";
        var llm = new ScriptedAgentLlm(
            FastReview(
                "clarify",
                "NONE",
                "NONE",
                question));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(FastReviewDuplicateSourceResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion = "Donne-moi les variantes documentées."
            },
            CancellationToken.None);

        var clarification = Assert.IsType<SourceBackedClarificationDecision>(
            result.Clarification);
        Assert.Equal(question, clarification.Message);
        Assert.Equal("clarify", result.JudgeDecision.Decision);
        Assert.Equal(
            "clarification_requested",
            result.ConversationMemory!.Outcome);
        Assert.Single(llm.Requests);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.clarification.requested"
            && trace.Fields["decision_source"] == "llm_evidence_judge"
            && trace.Fields["observed_candidates"] == "2");
    }

    [Fact]
    public async Task SingleSelectionScopeV2_AcceptsOneUsefulTechnicalProcedureWhenSeveralCandidatesMatch()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReviewWithReason(
                "accept",
                "E1",
                "",
                "Une seule procedure conforme suffit et E1 respecte toutes les contraintes visibles.",
                ("E1", "match"),
                ("E2", "match")),
            Completion(
                "Coupez l'alimentation pendant 10 secondes puis relancez le capteur [E1]."),
            SemanticReview("accept", "E1 décrit la coupure de dix secondes et le redémarrage."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleSelectionTechnicalProceduresResult()),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            SingleSelectionScopeIntake(
                "Donne-moi une procedure documentee pour relancer le capteur.",
                "une procedure technique exploitable"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Null(result.Clarification);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Equal(
            "source_backed_single_selection_scope_review_v2",
            llm.StructuredOutputContracts[1].Name);
        Assert.True(
            llm.StructuredOutputContracts[1].Schema
                .GetProperty("properties")
                .TryGetProperty("reason", out _));
        var scopePrompt = RequestText(llm, 1);
        Assert.Contains(
            "simple pluralite",
            scopePrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "information que seul",
            scopePrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "l'utilisateur peut fournir",
            scopePrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Une preference facultative",
            scopePrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "n'est pas une information manquante",
            scopePrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "decision=accept seulement si exactement un candidat est match",
            scopePrompt,
            StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["single_selection_scope_decision"] == "accept"
            && trace.Fields["single_selection_scope_basis"]
                == "multiple_matching_candidates_non_blocking"
            && trace.Fields["single_selection_scope_reason"].Contains(
                "Une seule procedure conforme suffit",
                StringComparison.Ordinal)
            && trace.Fields["single_selection_scope_normalized"] == "false"
            && trace.Fields["single_selection_scope_protocol_valid"] == "true");
    }

    [Fact]
    public async Task SingleSelectionScopeV2_AcceptsAVisibleNonFirstMatchWithoutOrderPreference()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E2",
                "E2"),
            SingleSelectionScopeReviewWithReason(
                "accept",
                "E2",
                "",
                "E2 est visible, correspond a la demande et un seul exemple suffit.",
                ("E1", "match"),
                ("E2", "match")),
            Completion(
                "Maintenez les deux commandes pendant 5 secondes [E2]."),
            SemanticReview("accept", "E2 décrit le maintien des deux commandes pendant cinq secondes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleSelectionTechnicalProceduresResult()),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            SingleSelectionScopeIntake(
                "Donne-moi un exemple de procedure documentee pour relancer le capteur.",
                "un exemple de procedure technique"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Null(result.Clarification);
        Assert.Equal("E2", Assert.Single(result.CitedEvidence).EvidenceId);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["single_selection_scope_decision"] == "accept"
            && trace.Fields["single_selection_scope_basis"]
                == "multiple_matching_candidates_non_blocking"
            && trace.Fields["single_selection_scope_normalized"] == "false");
    }

    [Fact]
    public async Task SingleSelectionScopeV2_ClarifiesWhenMissingRegionChangesTheCorrectComplianceLabel()
    {
        const string question =
            "Dans quelle region le produit sera-t-il utilise : Region Nord ou Region Sud ?";
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReviewWithReason(
                "clarify",
                "NONE",
                question,
                "La region d'utilisation determine laquelle des deux etiquettes est correcte.",
                ("E1", "match"),
                ("E2", "match")));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleSelectionComplianceLabelsResult()),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            SingleSelectionScopeIntake(
                "Quelle etiquette de conformite dois-je appliquer au produit ?",
                "une etiquette correcte pour le contexte d'utilisation"),
            CancellationToken.None);

        var clarification = Assert.IsType<SourceBackedClarificationDecision>(
            result.Clarification);
        Assert.Equal(question, clarification.Message);
        Assert.False(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["single_selection_scope_decision"] == "clarify"
            && trace.Fields["single_selection_scope_basis"]
                == "multiple_matching_candidates_blocking"
            && trace.Fields["single_selection_scope_reason"].Contains(
                "region d'utilisation determine",
                StringComparison.Ordinal)
            && trace.Fields["single_selection_scope_normalized"] == "false"
            && trace.Fields["single_selection_scope_protocol_valid"] == "true");
    }

    [Fact]
    public async Task SingleSelectionScopeV2_RejectsClarifyThatCarriesASelectedCandidate()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReviewWithReason(
                "clarify",
                "E1",
                "Quelle region faut-il appliquer ?",
                "La region manque.",
                ("E1", "match"),
                ("E2", "match")));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleSelectionComplianceLabelsResult()),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            SingleSelectionScopeIntake(
                "Quelle etiquette de conformite dois-je appliquer au produit ?",
                "une etiquette correcte pour le contexte d'utilisation"),
            CancellationToken.None);

        Assert.Null(result.Clarification);
        Assert.False(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["single_selection_scope_basis"]
                == "invalid_scope_review"
            && trace.Fields["single_selection_scope_protocol_valid"] == "false"
            && trace.Fields["single_selection_scope_normalized"] == "false");
    }

    [Fact]
    public async Task SingleSelectionScopeV2_RejectsAcceptWhenSelectedCandidateIsNotAMatch()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReviewWithReason(
                "accept",
                "E1",
                "",
                "Le candidat serait selectionne.",
                ("E1", "nonmatch"),
                ("E2", "match")));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleSelectionTechnicalProceduresResult()),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            SingleSelectionScopeIntake(
                "Donne-moi une procedure documentee pour relancer le capteur.",
                "une procedure technique exploitable"),
            CancellationToken.None);

        Assert.Null(result.Clarification);
        Assert.False(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["single_selection_scope_basis"]
                == "invalid_scope_review"
            && trace.Fields["single_selection_scope_protocol_valid"] == "false");
    }

    [Fact]
    public async Task SingleSelectionScopeV2_RejectsIncompleteCandidateClassification()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReviewWithReason(
                "accept",
                "E1",
                "",
                "E1 correspond a la demande.",
                ("E1", "match")));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleSelectionTechnicalProceduresResult()),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            SingleSelectionScopeIntake(
                "Donne-moi une procedure documentee pour relancer le capteur.",
                "une procedure technique exploitable"),
            CancellationToken.None);

        Assert.Null(result.Clarification);
        Assert.False(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["single_selection_scope_basis"]
                == "invalid_scope_review"
            && trace.Fields["single_selection_scope_protocol_valid"] == "false");
    }

    [Fact]
    public async Task AdaptiveFastReview_AnchorsInlineCitationToTheExplicitSourceWindowItem()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E2",
                "Ouvrez la section Typical Properties Powder."),
            SemanticReview(
                "accept",
                "Le passage choisi et son ancrage remappe repondent a la demande."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SamePageSourceWindowResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion =
                    "Quel passage dois-je ouvrir pour verifier la reponse ?"
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            "Ouvrez la section Typical Properties Powder [E2].",
            result.Answer);
        Assert.Equal(
            "properties-2",
            Assert.Single(result.CitedEvidence).ChunkId);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["selected_evidence_ids"] == "E2"
            && trace.Fields["anchor_verified"] == "true");
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task AdaptiveFastReview_GivesMechanicalFailureToSelectedEvidenceWriter()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            Completion("Je te propose le dessert au chocolat documente."),
            Completion("Je te propose le dessert au chocolat documente [E1]."),
            SemanticReview("accept", "E1 documente le dessert proposé, désormais cité."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(7,
                "Dessert au chocolat facile : mousse. Faites fondre le chocolat, "
                + "incorporez la crème puis laissez prendre deux heures au frais.")),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Contains(
            "CORRECTION MECANIQUE OBLIGATOIRE",
            RequestText(llm, 2));
        Assert.Contains("missing_citations", RequestText(llm, 2));
        Assert.Contains("[E#]", RequestText(llm, 2));
        Assert.Equal(
            new[] { false, true },
            result.TraceEvents
                .Where(static trace =>
                    trace.EventName == "source_backed_agent_v2.answer.verified")
                .Select(static trace => trace.Fields["valid"] == "true")
                .ToArray());
    }

    [Fact]
    public async Task AdaptiveFastReview_StopsMechanicalRepairWhenFailureSignatureDoesNotChange()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            Completion("Premiere formulation sans citation."),
            Completion("Deuxieme formulation toujours sans citation."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                MaximumTurns = 12,
                MaximumSemanticCorrectionTurns = 6
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.mechanical_repair.no_progress_stopped"
            && trace.Fields["failure_signature"].Contains(
                "missing_citations",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdaptiveFastReview_OffersOneCandidatePerVisibleSource()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E2"),
            SingleSelectionScopeReview(
                "accept",
                "E1",
                "",
                ("E1", "match"),
                ("E3", "nonmatch")),
            Completion("Je te propose les eclairs documentes [E2]."),
            SemanticReview("accept", "E2 appartient à la recette des éclairs sélectionnée."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(FastReviewDuplicateSourceResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("[E1]", RequestText(llm, 0));
        Assert.DoesNotContain("CANDIDAT [E2]", RequestText(llm, 0));
        Assert.Contains("EXTRAIT 2 [E2]", RequestText(llm, 0));
        Assert.Contains(
            "Creme patissiere et poche a douille.",
            RequestText(llm, 0));
        Assert.Contains("[E3]", RequestText(llm, 0));
        Assert.Contains(
            "Creme patissiere et poche a douille.",
            RequestText(llm, 2));
        var normalizedWriterPrompt = RequestText(llm, 2)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(
            "[E1]\nTITRE:",
            normalizedWriterPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "[E2]\nTITRE:",
            normalizedWriterPrompt,
            StringComparison.Ordinal);
        Assert.Equal("E2", Assert.Single(result.CitedEvidence).EvidenceId);
        Assert.Empty(llm.ToolSets[0]);
        Assert.Contains("action,", RequestText(llm, 0));
        Assert.Contains("evidenceId, anchorId, text", RequestText(llm, 0));
        Assert.Contains("180", RequestText(llm, 0));
        Assert.Equal(2, llm.StructuredOutputContracts.Count);
        var contract = llm.StructuredOutputContracts[0];
        Assert.Equal(
            "source_backed_fast_evidence_review_v1",
            contract.Name);
        Assert.Equal(
            new[] { "E1", "E3", "NONE" },
            contract.Schema
                .GetProperty("properties")
                .GetProperty("evidenceId")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        Assert.Equal(
            new[] { "E1", "E2", "E3", "NONE" },
            contract.Schema
                .GetProperty("properties")
                .GetProperty("anchorId")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
    }

    [Fact]
    public async Task AdaptiveFastReview_AllowsAnExactSiblingCitationAndReviewsItSemantically()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReview(
                "accept",
                "E1",
                "",
                ("E1", "match"),
                ("E3", "nonmatch")),
            Completion(
                "Les eclairs documentes utilisent une creme patissiere "
                + "et une poche a douille [E2]."),
            SemanticReview(
                "accept",
                "E2 soutient exactement le fait redige pour l'element choisi."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(FastReviewDuplicateSourceResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(
            result.IsSourceVerified,
            string.Join(", ", result.Verification?.Errors.Select(static error =>
                error.Code) ?? Array.Empty<string>()));
        Assert.Equal("E2", Assert.Single(result.CitedEvidence).EvidenceId);
        Assert.Equal("eclairs-2", result.CitedEvidence[0].ChunkId);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.answer.verified"
            && trace.Fields["writer_allowed_evidence_ids"] == "E1,E2"
            && trace.Fields["required_evidence_groups"] == "1"
            && trace.Fields["context_evidence_beyond_selection"] == "true");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task AdaptiveFastReview_PresentsEmbeddedSourceWindowInDocumentOrder()
    {
        const string methodText =
            "METHOD. Prepare the pastry shells on a clean tray and let them "
            + "cool completely before handling the filling. Measure every "
            + "component in the documented order. Whisk the cream until it "
            + "holds soft peaks, keep the bowl cold, then transfer the cream "
            + "to the prepared piping bag. Fill the shells with chocolate "
            + "cream and keep their order on the tray.";
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            Completion("Prepare the documented chocolate eclairs [E1]."),
            SemanticReview("accept", "La fenêtre source documente les éclairs au chocolat proposés."));
        var sourceWindowResult = Results(
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "doc-eclairs-window",
                        docName = "pastry-guide.pdf",
                        docPath = "Knowledge/pastry-guide.pdf",
                        pageStart = 34,
                        pageEnd = 34,
                        chunkId = "eclairs-method",
                        fullText = methodText,
                        score = 0.96,
                        retrievalQuery = "simple documented dessert",
                        sourceWindowAnchorChunkId = "eclairs-method",
                        sourceWindow = new[]
                        {
                            new
                            {
                                chunkId = "eclairs-title",
                                chunkIndex = 100,
                                pageStart = 34,
                                pageEnd = 34,
                                sectionTitle = "CHOCOLATE ECLAIRS",
                                headingPath =
                                    "PASTRY > CHOCOLATE ECLAIRS",
                                text =
                                    "CHOCOLATE ECLAIRS. A documented pastry "
                                    + "entry with a deliberately long neighboring "
                                    + "description used to validate compact source windows."
                            },
                            new
                            {
                                chunkId = "eclairs-components",
                                chunkIndex = 101,
                                pageStart = 34,
                                pageEnd = 34,
                                sectionTitle = string.Empty,
                                headingPath = string.Empty,
                                text = "COMPONENTS. Pastry shells and chocolate cream."
                            },
                            new
                            {
                                chunkId = "eclairs-method",
                                chunkIndex = 102,
                                pageStart = 34,
                                pageEnd = 34,
                                sectionTitle = string.Empty,
                                headingPath = string.Empty,
                                text = methodText
                            }
                        }
                    }
                }
            });
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(sourceWindowResult),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var judgePrompt = RequestText(llm, 0);
        Assert.True(
            judgePrompt.IndexOf(
                "CHOCOLATE ECLAIRS",
                StringComparison.Ordinal)
            < judgePrompt.IndexOf(
                "METHOD. Prepare the pastry shells",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            "COMPONENTS. Pastry shells",
            judgePrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Whisk the cream until it holds soft peaks",
            judgePrompt);
        Assert.Contains(
            "section=CHOCOLATE ECLAIRS",
            judgePrompt);
        Assert.Contains("EXTRAIT 2 [E1] (ancre)", judgePrompt);

        var writerPrompt = RequestText(llm, 1);
        Assert.DoesNotContain(
            "HANDOFF_SEMANTIQUE_DU_JUGE:",
            writerPrompt,
            StringComparison.Ordinal);
        Assert.True(
            writerPrompt.IndexOf(
                "CHOCOLATE ECLAIRS",
                StringComparison.Ordinal)
            < writerPrompt.IndexOf(
                "COMPONENTS. Pastry shells",
                StringComparison.Ordinal));
        var componentsIndex = writerPrompt.IndexOf(
            "COMPONENTS. Pastry shells",
            StringComparison.Ordinal);
        var methodIndex = writerPrompt.IndexOf(
            "METHOD. Prepare the pastry shells",
            StringComparison.Ordinal);
        Assert.True(
            componentsIndex < methodIndex,
            $"components={componentsIndex}; method={methodIndex}; prompt={writerPrompt}");
    }

    [Fact]
    public async Task AdaptiveFastReview_RechecksOnlyAfterTheEvidenceWindowChanges()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "research",
                "NONE",
                "NONE",
                "Aucune option simple dans ce lot."),
            Completion(Call(
                "second-search",
                "submit_research_action",
                new
                {
                    capability = "rag_search",
                    query = "option simple sans cuisson",
                    scope = "",
                    document = "document imagine.pdf",
                    anchor = "",
                    limit = 8,
                    offset = 0,
                    mode = "auto"
                })),
            FastReview(
                "writer",
                "E2",
                "E2"),
            Completion("Je propose l'option simple sans cuisson [E2]."),
            SemanticReview("accept", "E2 documente une option simple sans cuisson."));
        var executor = new ScriptedToolExecutor(
            SearchResultAtPage(
                1,
                "Option documentee avec plusieurs etapes complexes."),
            SearchResultAtPage(
                2,
                "Option simple et sans cuisson, directement utilisable."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(5, llm.Requests.Count);
        Assert.Contains(
            "RECHERCHES_DEJA_EFFECTUEES",
            RequestText(llm, 1));
        Assert.Contains(
            "PREUVES_REFUSEES_PAR_LE_JUGE",
            RequestText(llm, 1));
        Assert.Contains(
            "MEILLEURE_PISTE_A_APPROFONDIR",
            RequestText(llm, 1));
        Assert.Contains(
            "anchor=option-1",
            RequestText(llm, 1));
        Assert.Equal(
            "submit_research_action",
            Assert.Single(llm.ToolSets[1]).Name);
        Assert.Equal(
            new[]
            {
                "capability",
                "query",
                "scope",
                "document",
                "anchor",
                "navigationKind",
                "limit",
                "offset"
            },
            Assert.Single(llm.ToolSets[1])
                .Parameters
                .GetProperty("required")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        Assert.Equal(
            2,
            result.TraceEvents.Count(static trace =>
                trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.turn.completed"
            && trace.Fields.TryGetValue(
                "compact_follow_up",
                out var compactFollowUp)
            && compactFollowUp == "true");
        Assert.False(
            executor.Arguments[1].TryGetProperty(
                "docPath",
                out _));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.compact_follow_up.mechanical_adjustment"
            && trace.Fields["adjustment"].StartsWith(
                "ungrounded_document_reference_removed:",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdaptiveFastReview_BoundsTwoConsecutiveResearchZeroYieldVerdictsWithTerminalDecision()
    {
        const string insufficiencyReason =
            "Les deux lots observes ne contiennent pas l'option demandee.";
        var llm = new ScriptedAgentLlm(
            FastReview(
                "research",
                "NONE",
                "NONE",
                "Le premier lot ne contient pas l'option demandee."),
            Completion(Call(
                "second-search",
                "submit_research_action",
                new
                {
                    capability = "rag_search",
                    query = "option documentee demandee",
                    scope = "",
                    document = "",
                    anchor = "",
                    limit = 8,
                    offset = 0,
                    mode = "auto"
                })),
            FastReview(
                "research",
                "NONE",
                "NONE",
                "Le second lot ne contient toujours pas l'option demandee."),
            Completion(Call(
                "terminal-yield",
                "resolve_source_yield",
                new
                {
                    decision = "insufficiency",
                    message = insufficiencyReason
                })));
        var executor = new ScriptedToolExecutor(
            SearchResultAtPage(
                1,
                "Information documentaire sans rapport avec l'option demandee."),
            SearchResultAtPage(
                2,
                "Autre information documentaire sans rapport avec la demande."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal("insufficient_evidence", result.JudgeDecision.Decision);
        var userFacingReason = Assert.Single(
            result.JudgeDecision.MissingEvidenceNotes);
        Assert.DoesNotContain(insufficiencyReason, userFacingReason);
        Assert.Contains("2 actions documentaires", userFacingReason);
        Assert.Contains("aucune source citable non rejetée", userFacingReason);
        Assert.Equal(
            userFacingReason,
            Assert.Single(result.ConversationMemory!.SemanticReviewNotes));
        Assert.Equal(new[] { "rag.search", "rag.search" }, executor.ToolNames);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Equal(
            new[] { "resolve_source_yield" },
            llm.ToolSets[3].Select(static tool => tool.Name).ToArray());
        Assert.Equal(128, llm.MaxTokens[3]);
        Assert.Contains(
            "DECISION TERMINALE DE RENDEMENT",
            RequestText(llm, 3),
            StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.zero_yield.resolution_requested"
            && trace.Fields["consecutive_research_reviews"] == "2"
            && trace.Fields["executed_requests"] == "2"
            && trace.Fields["rejected_evidence"] == "2"
            && trace.Fields["decision_source"]
                == "llm_evidence_judge+mechanical_budget_contract");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_audit_zero_yield.terminal_decision_requested"
            && trace.Fields["continuations"] == "0"
            && trace.Fields["maximum_continuations"] == "0");
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName
                == "source_backed_agent_v2.source_insufficiency.declared"
            && trace.Fields["reason"] == insufficiencyReason);
    }

    [Theory]
    [InlineData(false, 3, false, true)]
    [InlineData(true, 3, false, false)]
    [InlineData(true, 0, false, true)]
    [InlineData(false, 3, true, false)]
    public void SemanticYieldTerminalDecision_DoesNotWaitForCandidatesWithoutAnAuditPath(
        bool candidateAuditEnabled,
        int pendingSemanticCandidateCount,
        bool dedicatedCandidateAuditTurn,
        bool expected)
    {
        var actual = SourceBackedAgentV2Runner
            .ShouldRequestSemanticYieldTerminalDecisionForTests(
                flatEvidenceGapTerminalDecisionNextTurn: false,
                semanticYieldResolutionPending: true,
                semanticYieldResolutionContinuationCount: 0,
                semanticYieldResolutionContinuationLimit: 0,
                candidateAuditEnabled,
                pendingSemanticCandidateCount,
                dedicatedCandidateAuditTurn);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task AdaptiveFastReview_TerminatesWhenUnauditedCandidatesHaveNoAuditPath()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "research",
                "NONE",
                "NONE",
                "Le premier lot ne contient pas la preuve demandee."),
            Completion(Call(
                "second-search",
                "submit_research_action",
                new
                {
                    capability = "rag_search",
                    query = "seconde recherche generique",
                    scope = "",
                    document = "",
                    anchor = "",
                    limit = 8,
                    offset = 0,
                    mode = "auto"
                })),
            FastReview(
                "research",
                "NONE",
                "NONE",
                "Le second lot ne contient toujours pas la preuve demandee."),
            Completion(Call(
                "terminal-yield",
                "resolve_source_yield",
                new
                {
                    decision = "insufficiency",
                    message = "Les lots observes restent insuffisants."
                })));
        var executor = new ScriptedToolExecutor(
            FastReviewWideBatchResult(1),
            FastReviewWideBatchResult(6));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal("insufficient_evidence", result.JudgeDecision.Decision);
        Assert.Equal(new[] { "rag.search", "rag.search" }, executor.ToolNames);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Equal(
            new[] { "resolve_source_yield" },
            llm.ToolSets[3].Select(static tool => tool.Name).ToArray());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_audit_zero_yield.terminal_decision_requested"
            && trace.Fields["candidate_audit_enabled"] == "false"
            && trace.Fields["pending_candidates"] == "4");
    }

    [Theory]
    [InlineData(
        "fr",
        1,
        0,
        "Après 1 action documentaire, le juge LLM a conclu qu'aucune source citable non rejetée ne suffisait au livrable demandé.")]
    [InlineData(
        "fr",
        2,
        1,
        "Après 2 actions documentaires, le juge LLM a conclu que la seule source citable non rejetée ne suffisait au livrable demandé.")]
    [InlineData(
        "en",
        1,
        1,
        "After 1 document-retrieval action, the LLM judge concluded that the single non-rejected citable source was insufficient for the requested deliverable.")]
    [InlineData(
        "en",
        2,
        2,
        "After 2 document-retrieval actions, the LLM judge concluded that the 2 non-rejected citable sources were insufficient for the requested deliverable.")]
    public void TerminalInsufficiencyNote_IsGroundedAndLocalized(
        string language,
        int executedRequests,
        int observedSources,
        string expected)
    {
        var intake = Intake("Question documentaire") with
        {
            Language = language
        };

        var note = SourceBackedAgentV2Runner
            .BuildMechanicallyGroundedTerminalInsufficiencyNote(
                intake,
                executedRequests,
                observedSources);

        Assert.Equal(expected, note);
    }

    [Fact]
    public async Task AdaptiveFastReview_ExpandsExactLeadContextWithoutAnotherDecisionCall()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "context",
                "E1",
                "E1",
                "Les ingredients sont visibles mais la methode manque."),
            FastReview(
                "writer",
                "E2",
                "E2"),
            Completion(
                "Faites fondre le chocolat, incorporez la creme puis laissez prendre deux heures au frais [E2]."),
            SemanticReview("accept", "E2 fournit les opérations et les deux heures de prise reprises dans le texte."));
        var executor = new ScriptedToolExecutor(
            SearchResultAtPage(
                117,
                "Mousse facile au chocolat. Ingredients: chocolat et creme."),
            DocumentContextResultAtPage(
                117,
                "Mousse facile au chocolat. Ingredients: chocolat et creme. "
                + "Faites fondre le chocolat, incorporez la creme puis laissez "
                + "prendre deux heures au frais."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "documents.context" },
            executor.ToolNames);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Contains(
            "methode actionnable",
            RequestText(llm, 0));
        Assert.Contains(
            "Faites fondre le chocolat",
            RequestText(llm, 1));
        Assert.Equal(
            "Knowledge/Options.pdf",
            executor.Arguments[1].GetProperty("docRef").GetString());
        Assert.Equal(
            "option-117",
            executor.Arguments[1].GetProperty("chunkId").GetString());
        Assert.Equal(
            117,
            executor.Arguments[1].GetProperty("pageStart").GetInt32());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.next_action_scheduled"
            && trace.Fields["decision_source"] == "llm_evidence_judge"
            && trace.Fields["capability"] == "documents_context"
            && trace.Fields["lead_evidence_ids"] == "E1");
        Assert.DoesNotContain(llm.ToolSets, static tools =>
            tools.Any(tool => tool.Name == "submit_research_action"));
    }

    [Fact]
    public async Task AdaptiveFastReview_DoesNotWriteFromAnAnchorOutsideTheSelectedCandidate()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E999"),
            Completion(Call(
                "repair-action",
                "submit_research_action",
                new
                {
                    capability = "documents_context",
                    document = "Knowledge/Options.pdf",
                    anchor = "option-117",
                    pageStart = 117,
                    pageEnd = 117,
                    limit = 8,
                    offset = 0
                })),
            FastReview(
                "writer",
                "E2",
                "E2"),
            Completion(
                "Faites fondre le chocolat, incorporez la creme puis laissez prendre deux heures au frais [E2]."),
            SemanticReview("accept", "E2 fournit les opérations et les deux heures de prise reprises dans le texte."));
        var executor = new ScriptedToolExecutor(
            SearchResultAtPage(
                117,
                "Mousse facile au chocolat. Ingredients: chocolat et creme."),
            DocumentContextResultAtPage(
                117,
                "Mousse facile au chocolat. Ingredients: chocolat et creme. "
                + "Faites fondre le chocolat, incorporez la creme puis laissez "
                + "prendre deux heures au frais."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "documents.context" },
            executor.ToolNames);
        Assert.Equal(5, llm.Requests.Count);
        Assert.Contains(
            "identifiant d'extrait",
            RequestText(llm, 1));
        Assert.Equal(
            "submit_research_action",
            Assert.Single(llm.ToolSets[1]).Name);
        var reviews = result.TraceEvents
            .Where(static trace =>
                trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed")
            .ToArray();
        Assert.Equal(2, reviews.Length);
        Assert.Equal("false", reviews[0].Fields["protocol_valid"]);
        Assert.Equal("true", reviews[1].Fields["protocol_valid"]);
        Assert.Equal(
            "NONE",
            reviews[0].Fields["anchor_excerpt"]);
    }

    [Fact]
    public async Task AdaptiveFastReview_KeepsUnrejectedCandidatesVisibleAfterInvalidReview()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E999"),
            Completion(Call(
                "second-search",
                "submit_research_action",
                new
                {
                    capability = "rag_search",
                    query = "autre option documentee",
                    scope = "",
                    document = "",
                    anchor = "",
                    limit = 3,
                    offset = 0,
                    mode = "auto"
                })),
            FastReview(
                "writer",
                "E2",
                "E2"),
            SingleSelectionScopeReview(
                "accept",
                "E2",
                "",
                ("E1", "nonmatch"),
                ("E2", "match")),
            Completion("Je propose la seconde option documentee [E2]."),
            SemanticReview("accept", "La seconde option est documentée par E2."));
        var executor = new ScriptedToolExecutor(
            SearchResultAtPage(
                1,
                "Premiere option documentee encore non rejetee."),
            SearchResultAtPage(
                2,
                "Seconde option documentee nouvellement observee."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "rag.search" },
            executor.ToolNames);
        Assert.Contains("CANDIDAT [E2]", RequestText(llm, 2));
        Assert.Contains("CANDIDAT [E1]", RequestText(llm, 2));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["protocol_valid"] == "false"
            && trace.Fields["lead_evidence_ids"] == "E1");
    }

    [Fact]
    public async Task AdaptiveFastReview_RedirectsOversizedInlineAnswerToWriter()
    {
        var oversizedAnswer = string.Join(
            " ",
            Enumerable.Repeat(
                "La methode documentee reste ancree sur la preuve selectionnee.",
                8));
        Assert.True(oversizedAnswer.Length > 300);
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                oversizedAnswer),
            Completion(
                "Suivez la methode documentee pour le dessert selectionne [E1]."),
            SemanticReview("accept", "E1 documente la méthode pour la mousse proposée."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(7,
                "Dessert au chocolat facile : mousse. Faites fondre le chocolat, "
                + "incorporez la crème puis laissez prendre deux heures au frais.")),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Contains(
            "PREUVES EXACTEMENT AUTORISEES",
            RequestText(llm, 1));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["selected_evidence_ids"] == "E1"
            && trace.Fields["answer_redirected_to_writer"] == "true");
    }

    [Fact]
    public async Task AdaptiveFastReview_PresentsAllCarriedCandidatesWithNewBatch()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E999"),
            Completion(Call(
                "second-search",
                "submit_research_action",
                new
                {
                    capability = "rag_search",
                    query = "second lot documente",
                    scope = "",
                    document = "",
                    anchor = "",
                    limit = 3,
                    offset = 0,
                    mode = "auto"
                })),
            FastReview(
                "clarify",
                "NONE",
                "NONE",
                "Quel candidat documente souhaitez-vous retenir ?"));
        var executor = new ScriptedToolExecutor(
            FastReviewDistinctBatchResult(1),
            FastReviewDistinctBatchResult(4));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.NotNull(result.Clarification);
        var secondReviewPrompt = RequestText(llm, 2);
        foreach (var evidenceId in new[] { "E1", "E2", "E3", "E4", "E5", "E6" })
            Assert.Contains($"CANDIDAT [{evidenceId}]", secondReviewPrompt);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["presented_candidate_ids"]
                == "E1,E2,E3,E4,E5,E6");
    }

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
    public async Task NativeLoop_PreservesEvidenceExplicitlyRetainedByTheLlmAcrossCompaction()
    {
        var cards = Enumerable.Range(1, 12)
            .Select(static index => new MealCard(
                "Dessert documente " + index,
                index))
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: une proposition sourcee\nDIMENSIONS: proposition\n"
                + "PREUVES_ATOMIQUES: 1 proposition\n"
                + "INTENTIONS_RECHERCHE: dessert chocolat facile\n"
                + "APPROCHE_OUTILS: rag_search puis inventaire si necessaire\n"
                + "PREMIERE_ACTION: aucune\n"
                + "ACCEPTER_SI: proposition citee\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune proposition"),
            Completion(Call("search-1", "rag_search", new
            {
                query = "dessert chocolat facile"
            })),
            new SourceBackedAgentCompletion(
                string.Empty,
                new[]
                {
                    Call("workspace-1", "manage_evidence_workspace", new
                    {
                        retainEvidenceIds = new[] { "E1" },
                        rejectEvidenceIds = Array.Empty<string>(),
                        note = "Cette preuve repond directement a la demande."
                    }),
                    Call("cards-1", "documents_content_cards", new
                    {
                        categoryPath = "Cuisine",
                        limit = 12,
                        offset = 0
                    })
                },
                "tool_calls",
                200,
                40),
            Completion("Le dessert documente est propose [E1]."),
            SemanticReview("accept", "E1 soutient directement la proposition."));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            ContentCardInventoryResult(cards));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumWorkingEvidenceItems = 8
            });

        var result = await runner.RunAsync(
            Intake("Je veux un dessert au chocolat facile."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "documents.content_cards" },
            executor.ToolNames);
        Assert.Contains(
            "E1 | rag_hit | statut=retenue_par_ta_decision",
            RequestText(llm, 3));
        Assert.Contains(
            "Cette preuve repond directement a la demande.",
            RequestText(llm, 3));
        Assert.Contains(llm.ToolSets[2], static tool =>
            tool.Name == "manage_evidence_workspace");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.evidence_workspace.updated"
            && trace.Fields["retained_evidence_ids"] == "E1");
    }

    [Fact]
    public async Task NativeLoop_StopsReproposingEvidenceExplicitlyRejectedByTheLlm()
    {
        var cards = Enumerable.Range(1, 12)
            .Select(static index => new MealCard(
                "Dessert documente " + index,
                index))
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: une proposition sourcee\nDIMENSIONS: proposition\n"
                + "PREUVES_ATOMIQUES: 1 proposition\n"
                + "INTENTIONS_RECHERCHE: dessert\n"
                + "APPROCHE_OUTILS: inventaire\n"
                + "PREMIERE_ACTION: aucune\n"
                + "ACCEPTER_SI: proposition citee\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune proposition"),
            Completion(Call("cards-1", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 12,
                offset = 0
            })),
            Completion(Call("workspace-1", "manage_evidence_workspace", new
            {
                retainEvidenceIds = new[] { "E2" },
                rejectEvidenceIds = new[] { "E1" },
                note = "E2 correspond; E1 ne correspond pas a la demande."
            })),
            Completion("Le second dessert est propose [E2]."),
            SemanticReview("accept", "E2 soutient directement la proposition."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumWorkingEvidenceItems = 8
            });

        var result = await runner.RunAsync(
            Intake("Propose un dessert documente."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var compactState = RequestText(llm, 3);
        Assert.Contains(
            "PREUVES REFUSEES PAR TES DECISIONS SEMANTIQUES",
            compactState);
        Assert.Contains(
            "E1 | valeur=Dessert documente 1",
            compactState);
        Assert.DoesNotContain("- E1 | canonical_content_card", compactState);
        Assert.Contains(
            "E2 | canonical_content_card | statut=retenue_par_ta_decision",
            compactState);
        Assert.DoesNotContain("[E1]", result.Answer);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.evidence_workspace.updated"
            && trace.Fields["rejected_evidence_ids"] == "E1");
    }

    [Fact]
    public async Task NativeLoop_RejectsAnUnchangedWorkspaceUpdateAsANoOp()
    {
        var unchangedWorkspaceUpdate = new
        {
            retainEvidenceIds = Array.Empty<string>(),
            rejectEvidenceIds = new[] { "E1" },
            note = "Cette preuve ne repond pas directement a la demande."
        };
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: une proposition sourcee\n"
                + "PREUVES_ATOMIQUES: 1 proposition"),
            Completion(Call("search-1", "rag_search", new
            {
                query = "dessert chocolat facile"
            })),
            Completion(Call(
                "workspace-1",
                "manage_evidence_workspace",
                unchangedWorkspaceUpdate)),
            Completion(Call(
                "workspace-2",
                "manage_evidence_workspace",
                unchangedWorkspaceUpdate)),
            Completion(Call("search-2", "rag_search", new
            {
                query = "dessert vanille facile"
            })),
            Completion("Le dessert documente est propose [E2]."),
            SemanticReview("accept", "E2 soutient directement la proposition."));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            SearchResultAtPage(
                43,
                "Une recette simple de dessert a la vanille est documentee ici."));
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Je veux un dessert au chocolat facile."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains("duplicate_tool_call", RequestText(llm, 4));
        Assert.Single(
            result.TraceEvents,
            static trace =>
                trace.EventName
                == "source_backed_agent_v2.evidence_workspace.updated");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.evidence_workspace.duplicate_rejected");
    }

    [Fact]
    public async Task NativeLoop_RejectsExactDuplicateCallWithoutReexecutingBackend()
    {
        var repeated = Call("search-1", "rag_search", new { query = "couple serrage HX-42" });
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: valeur prescrite sourcee."),
            Completion(repeated),
            Completion(repeated with { Id = "search-2" }),
            Completion("Le couple prescrit est de 85 N·m [E1]."),
            SemanticReview("accept", "La reponse est complete et soutenue."));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(Intake("Quel est le couple prescrit pour HX-42 ?"), CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Single(executor.ToolNames);
        Assert.Contains("duplicate_tool_call", RequestText(llm, 3));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.duplicate_rejected");
    }

    [Fact]
    public async Task NativeLoop_RejectsExactDuplicatePaginatedRouteWithoutChoosingTheNextPage()
    {
        var repeated = Call("cards-1", "documents_content_cards", new
        {
            categoryPath = "Cuisine",
            limit = 5,
            offset = 0
        });
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option documentee."),
            Completion(repeated),
            Completion(repeated with { Id = "cards-2" }),
            Completion("L'option Sticks de feta est documentee [E1]."),
            SemanticReview("accept", "E1 soutient directement l'option."));
        var executor = new ScriptedToolExecutor(
            PagedContentCardInventoryResult(),
            ContentCardInventoryResult());
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Single(executor.ToolNames);
        Assert.Equal(0, executor.Arguments[0].GetProperty("offset").GetInt32());
        Assert.Contains("duplicate_tool_call", RequestText(llm, 3));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.tool.duplicate_rejected");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.duplicate_advanced_to_next_page");
    }

    [Fact]
    public async Task NativeLoop_ExecutesNextPageOnlyAfterTheModelExplicitlySelectsIt()
    {
        var repeated = Call("cards-1", "documents_content_cards", new
        {
            categoryPath = "Cuisine",
            limit = 5,
            offset = 0
        });
        var explicitNext = Call("cards-3", "documents_content_cards", new
        {
            categoryPath = "Cuisine",
            limit = 5,
            offset = 5
        });
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option documentee."),
            Completion(repeated),
            Completion(repeated with { Id = "cards-2" }),
            Completion(explicitNext),
            Completion("L'option Sticks de feta est documentee [E1]."),
            SemanticReview("accept", "E1 soutient directement l'option."));
        var executor = new ScriptedToolExecutor(
            PagedContentCardInventoryResult(), ContentCardInventoryResult());
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(Intake("Donne une option documentee."), CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains("duplicate_tool_call", RequestText(llm, 3));
        Assert.Equal(5, executor.Arguments[1].GetProperty("offset").GetInt32());
        Assert.Equal(explicitNext.Arguments.GetRawText(), executor.Arguments[1].GetRawText());
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.duplicate_advanced_to_next_page");
    }

    [Fact]
    public async Task SplitActionWriter_BoundsSimpleDuplicateLoopWithLlmDecisionOnlyTurn()
    {
        var repeated = Call("search-1", "rag_search", new
        {
            query = "dessert au chocolat facile recette"
        });
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: une proposition sourcee\nDIMENSIONS: aucune matrice\n"
                + "PREUVES_ATOMIQUES: 1 recette\nINTENTIONS_RECHERCHE: dessert chocolat\n"
                + "APPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: une recette directement soutenue\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune recette exploitable"),
            Completion(repeated),
            Completion(repeated with { Id = "search-2" }),
            Completion(repeated with { Id = "search-3" }),
            Completion("PRET_A_REDIGER: E1 soutient directement la proposition."),
            Completion("Je te propose le dessert au chocolat documente [E1]."),
            SemanticReview("accept", "E1 soutient directement la proposition."));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Je veux un dessert au chocolat facile, tu proposes quoi ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Single(executor.ToolNames);
        Assert.Empty(llm.ToolSets[4]);
        Assert.False(llm.RequireToolCalls[4]);
        Assert.Contains(
            "BUDGET MECANIQUE SANS PROGRES",
            RequestText(llm, 4),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.documentary_no_progress.decision_requested"
            && trace.Fields["consecutive_no_progress_turns"] == "2");
    }

    [Fact]
    public void NoProgressResolution_ExposesAnExplicitSourceInsufficiencyDecision()
    {
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildCompactSingleFollowUpTools",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var ordinaryTools = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentToolDefinition>>(
            method!.Invoke(
                null,
                new object[]
                {
                    Array.Empty<SourceBackedAgentToolDefinition>(),
                    false
                }));
        var resolutionTools = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentToolDefinition>>(
            method.Invoke(
                null,
                new object[]
                {
                    Array.Empty<SourceBackedAgentToolDefinition>(),
                    true
                }));

        Assert.DoesNotContain(
            ordinaryTools,
            static tool => tool.Name == "declare_source_insufficiency");
        Assert.Contains(
            resolutionTools,
            static tool => tool.Name == "declare_source_insufficiency");
        Assert.Contains(
            resolutionTools,
            static tool => tool.Name == "request_user_clarification");
        Assert.Contains(
            resolutionTools,
            static tool => tool.Name == "submit_research_action");
    }

    [Fact]
    public void SourceInsufficiency_IsRejectedOutsideAYieldResolutionTurn()
    {
        const string arguments =
            "{\"reason\":\"Les routes observées ne fournissent pas la preuve demandée.\"}";

        var outsideResolution = SourceBackedAgentV2Runner
            .ReadSourceInsufficiencyForTests(
                arguments,
                yieldResolutionAllowed: false);
        var withoutObservation = SourceBackedAgentV2Runner
            .ReadSourceInsufficiencyForTests(
                arguments,
                yieldResolutionAllowed: true,
                executedRequestCount: 0);

        Assert.False(outsideResolution.Accepted);
        Assert.Equal(
            "source_insufficiency_requires_yield_resolution_turn",
            outsideResolution.FailureReason);
        Assert.False(withoutObservation.Accepted);
        Assert.Equal(
            "source_insufficiency_requires_corpus_observation",
            withoutObservation.FailureReason);
    }

    [Fact]
    public async Task CandidateCollection_CanDeclareSourceInsufficiencyAfterRepeatedNoProgress()
    {
        var initial = Call("cards-initial", "documents_content_cards", new
        {
            categoryPath = "Knowledge",
            q = "target",
            limit = 2,
            offset = 0
        });
        var intake = Intake("Trouve trois éléments documentés sur un point absent.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initial.Id,
                    initial.Name,
                    initial.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "trois éléments documentés",
                    structuredLayout = false,
                    atomicEvidenceCount = 3,
                    atomicEvidenceType = "éléments documentés",
                    initialCapability = "documents_content_cards",
                    initialQuery = "target",
                    initialScopeId = 0,
                    initialLimit = 2
                }),
                "llm_router")
        };
        const string insufficiencyReason =
            "Les routes observées ne contiennent aucun passage citable sur le point demandé.";
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "audit-initial",
                Array.Empty<string>(),
                new[] { "E1", "E2" })),
            Completion(Call(
                "zero-yield-1",
                "start_content_card_research",
                new
                {
                    query = "target absent",
                    inventoryMode = "ordered",
                    limit = 2
                })),
            Completion(Call(
                "zero-yield-2",
                "start_content_card_research",
                new
                {
                    query = "target absent autre formulation",
                    inventoryMode = "representative",
                    limit = 2
                })),
            Completion(Call(
                "declare-insufficient",
                "declare_source_insufficiency",
                new
                {
                    reason = insufficiencyReason
                })));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Document contextuel A", 1),
                new MealCard("Document contextuel B", 2)
            }),
            new ToolResults(),
            new ToolResults());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumWorkingEvidenceItems = 8,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                SemanticCandidateStrategyEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal("insufficient_evidence", result.JudgeDecision.Decision);
        Assert.Null(result.FinalDraft);
        Assert.Equal(
            new[]
            {
                "documents.content_cards",
                "documents.content_cards",
                "documents.content_cards"
            },
            executor.ToolNames);
        var resolutionToolSetIndex = llm.ToolSets.FindIndex(static tools =>
            tools.Any(static tool =>
                tool.Name == "declare_source_insufficiency"));
        Assert.Equal(3, resolutionToolSetIndex);
        Assert.Contains(
            "RECHERCHES_DEJA_EFFECTUEES",
            RequestText(llm, resolutionToolSetIndex),
            StringComparison.Ordinal);
        Assert.True(
            RequestText(llm, resolutionToolSetIndex)
                .Split("nouvelles_preuves=0", StringSplitOptions.None)
                .Length >= 3);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName
                == "source_backed_agent_v2.source_insufficiency.declared"
            && trace.Fields["consecutive_no_progress_turns"] == "2"
            && trace.Fields["decision_source"] == "llm_orchestrator"
            && trace.Fields["reason"] == insufficiencyReason);
        Assert.Contains(
            "ne suffisent pas",
            SourceBackedTerminalAnswer.Build(result),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CandidateCollection_CanResolveAZeroYieldSemanticAuditBeforeAnotherSearch()
    {
        var initial = Call("cards-initial", "documents_content_cards", new
        {
            categoryPath = "Knowledge",
            q = "initial context",
            limit = 1,
            offset = 0
        });
        var intake = Intake("Trouve trois éléments documentés sur un point absent.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initial.Id,
                    initial.Name,
                    initial.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "trois éléments documentés",
                    structuredLayout = false,
                    atomicEvidenceCount = 3,
                    atomicEvidenceType = "éléments documentés",
                    initialCapability = "documents_content_cards",
                    initialQuery = "initial context",
                    initialScopeId = 0,
                    initialLimit = 1
                }),
                "llm_router")
        };
        const string insufficiencyReason =
            "Les quatre candidats observés ont été rejetés et aucune preuve du point demandé n'est disponible.";
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "audit-initial",
                Array.Empty<string>(),
                new[] { "E1" })),
            Completion(Call(
                "second-route",
                "start_content_card_research",
                new
                {
                    query = "point demandé",
                    inventoryMode = "representative",
                    limit = 3
                })),
            Completion(CandidateAuditCall(
                "audit-second",
                Array.Empty<string>(),
                new[] { "E2", "E3", "E4" })),
            Completion(Call(
                "declare-after-audit",
                "declare_source_insufficiency",
                new
                {
                    reason = insufficiencyReason
                })));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Contexte non pertinent", 1)
            }),
            ContentCardInventoryResult(new[]
            {
                new MealCard("Candidat rejeté A", 2),
                new MealCard("Candidat rejeté B", 3),
                new MealCard("Candidat rejeté C", 4)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumWorkingEvidenceItems = 8,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                SemanticCandidateStrategyEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal("insufficient_evidence", result.JudgeDecision.Decision);
        Assert.Equal(
            new[]
            {
                "documents.content_cards",
                "documents.content_cards"
            },
            executor.ToolNames);
        var resolutionToolSetIndex = llm.ToolSets.FindIndex(static tools =>
            tools.Any(static tool =>
                tool.Name == "declare_source_insufficiency"));
        Assert.Equal(3, resolutionToolSetIndex);
        var resolutionPrompt = RequestText(llm, resolutionToolSetIndex);
        Assert.Contains("audit_llm_approuvees=0", resolutionPrompt);
        Assert.Contains("audit_llm_refusees=3", resolutionPrompt);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_audit_zero_yield.resolution_requested"
            && trace.Fields["approved"] == "0"
            && trace.Fields["rejected"] == "4"
            && trace.Fields["required"] == "3");
    }

    [Fact]
    public async Task CandidateCollection_BoundsPersistentSemanticYieldResolutionAfterOneContinuation()
    {
        var initial = Call("cards-initial", "documents_content_cards", new
        {
            categoryPath = "Knowledge",
            q = "initial context",
            limit = 1,
            offset = 0
        });
        var intake = Intake("Trouve trois éléments documentés sur un point absent.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initial.Id,
                    initial.Name,
                    initial.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "trois éléments documentés",
                    structuredLayout = false,
                    atomicEvidenceCount = 3,
                    atomicEvidenceType = "éléments documentés",
                    initialCapability = "documents_content_cards",
                    initialQuery = "initial context",
                    initialScopeId = 0,
                    initialLimit = 1
                }),
                "llm_router")
        };
        const string insufficiencyReason =
            "Les candidats audités ont été refusés et la navigation restante ne fournit que des pointeurs.";
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "audit-initial",
                Array.Empty<string>(),
                new[] { "E1" })),
            Completion(Call(
                "second-route",
                "start_content_card_research",
                new
                {
                    query = "point demandé",
                    inventoryMode = "representative",
                    limit = 3
                })),
            Completion(CandidateAuditCall(
                "audit-second",
                Array.Empty<string>(),
                new[] { "E2", "E3", "E4" })),
            Completion(Call(
                "continue-with-navigation",
                "submit_research_action",
                new
                {
                    capability = "documents_navigation",
                    query = "",
                    scope = "Manuals",
                    document = "HydraulicPumpManual.pdf",
                    anchor = "",
                    navigationKind = "title_anchor",
                    inventoryMode = "",
                    limit = 5,
                    offset = 0,
                    mode = "focused"
                })),
            Completion(Call(
                "declare-after-navigation",
                "resolve_source_yield",
                new
                {
                    decision = "insufficiency",
                    message = insufficiencyReason
                })));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Contexte non pertinent", 1)
            }),
            ContentCardInventoryResult(new[]
            {
                new MealCard("Candidat rejeté A", 2),
                new MealCard("Candidat rejeté B", 3),
                new MealCard("Candidat rejeté C", 4)
            }),
            Results(
                "documents.navigation",
                new
                {
                    navigationOnly = true,
                    total = 0,
                    items = Array.Empty<object>()
                }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 8,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                SemanticCandidateStrategyEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal("insufficient_evidence", result.JudgeDecision.Decision);
        Assert.Equal(
            new[]
            {
                "documents.content_cards",
                "documents.content_cards",
                "documents.navigation"
            },
            executor.ToolNames);
        Assert.Contains(
            llm.ToolSets[3],
            static tool => tool.Name == "declare_source_insufficiency");
        Assert.Contains(
            llm.ToolSets[3],
            static tool => tool.Name == "declare_source_insufficiency");
        Assert.Equal(
            new[] { "resolve_source_yield" },
            llm.ToolSets[4].Select(static tool => tool.Name).ToArray());
        Assert.DoesNotContain(
            llm.ToolSets[4],
            static tool => tool.Name == "submit_research_action");
        Assert.Equal(128, llm.MaxTokens[4]);
        var terminalRequest = RequestText(llm, 4);
        Assert.Contains(
            "DECISION TERMINALE DE RENDEMENT",
            terminalRequest,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Choisis exactement un nouvel outil utile",
            terminalRequest,
            StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_audit_zero_yield.resolution_persisted"
            && trace.Fields["decision_source"] == "mechanical_state_contract");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_audit_zero_yield.terminal_decision_requested"
            && trace.Fields["continuations"] == "1"
            && trace.Fields["decision_source"] == "mechanical_budget_contract");
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName
                == "source_backed_agent_v2.source_insufficiency.declared"
            && trace.Fields["reason"] == insufficiencyReason);
    }

    [Fact]
    public void SemanticAuditZeroYieldResolution_RequiresACompleteMechanicalTrigger()
    {
        Assert.True(SourceBackedAgentV2Runner
            .ShouldRequestSemanticAuditZeroYieldResolutionForTests(
                approvedCount: 0,
                rejectedCount: 3,
                requiredCount: 3,
                executedRequestCount: 2));
        Assert.False(SourceBackedAgentV2Runner
            .ShouldRequestSemanticAuditZeroYieldResolutionForTests(
                approvedCount: 1,
                rejectedCount: 3,
                requiredCount: 3,
                executedRequestCount: 2));
        Assert.True(SourceBackedAgentV2Runner
            .ShouldRequestSemanticAuditZeroYieldResolutionForTests(
                approvedCount: 0,
                rejectedCount: 2,
                requiredCount: 3,
                executedRequestCount: 2));
        Assert.False(SourceBackedAgentV2Runner
            .ShouldRequestSemanticAuditZeroYieldResolutionForTests(
                approvedCount: 0,
                rejectedCount: 0,
                requiredCount: 7,
                executedRequestCount: 3));
        Assert.False(SourceBackedAgentV2Runner
            .ShouldRequestSemanticAuditZeroYieldResolutionForTests(
                approvedCount: 0,
                rejectedCount: 3,
                requiredCount: 3,
                executedRequestCount: 1));
        Assert.False(SourceBackedAgentV2Runner
            .ShouldRequestSemanticAuditZeroYieldResolutionForTests(
                approvedCount: 0,
                rejectedCount: 3,
                requiredCount: 3,
                executedRequestCount: 2,
                hasRequiredEvidence: true));
    }

    [Fact]
    public void SemanticAuditZeroYieldContinuationBudget_ClosesOversampledAuditImmediately()
    {
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "DetermineSemanticYieldResolutionContinuationLimit",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(1, method!.Invoke(null, new object[] { 3, 3, 2 }));
        Assert.Equal(0, method.Invoke(null, new object[] { 4, 7, 3 }));
        Assert.Equal(0, method.Invoke(null, new object[] { 6, 3, 2 }));
        Assert.Equal(0, method.Invoke(null, new object[] { 40, 3, 2 }));
    }

    [Fact]
    public async Task CandidateCollection_UsesCompactLlmRecoveryAfterRepeatedNoProgress()
    {
        var repeated = Call("cards-initial", "documents_content_cards", new
        {
            categoryPath = "Knowledge",
            q = "option documentee",
            limit = 2,
            offset = 0
        });
        var zeroYield = Call("cards-zero-yield", "documents_content_cards", new
        {
            categoryPath = "Knowledge",
            q = "autre piste sans resultat",
            limit = 2,
            offset = 0
        });
        var intake = Intake("Donne quatre options documentees distinctes.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    repeated.Id,
                    repeated.Name,
                    repeated.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "quatre options documentees distinctes",
                    structuredLayout = false,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "options documentees",
                    initialCapability = "documents_content_cards",
                    initialQuery = "option documentee",
                    initialScopeId = 0,
                    initialLimit = 2
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "audit-initial",
                new[] { "E1", "E2" },
                Array.Empty<string>())),
            Completion(Call(
                "flat-adequacy-continue",
                "submit_flat_evidence_gap",
                new
                {
                    usefulEvidenceIds = new[] { "E1", "E2" },
                    missingRequirements = new[]
                    {
                        "Deux options documentees supplementaires"
                    },
                    reason = "Deux preuves ne suffisent pas encore pour les quatre options demandees."
                })),
            Completion(zeroYield),
            Completion(zeroYield with { Id = "cards-zero-yield-duplicate" }),
            Completion(Call("recovery", "submit_research_action", new
            {
                capability = "documents_content_cards",
                query = "",
                scope = "Knowledge",
                document = "",
                anchor = "",
                navigationKind = "",
                inventoryMode = "representative",
                limit = 4,
                offset = 0
            })),
            Completion(CandidateAuditCall(
                "audit-recovery",
                new[] { "E3", "E4" },
                Array.Empty<string>())),
            Completion(Call(
                "selection",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1", "E2", "E3", "E4" }
                })),
            Completion(
                "Options: Instance 1 [E1], Instance 2 [E2], "
                + "Instance 3 [E3] et Instance 4 [E4]."),
            SemanticReview(
                "accept",
                "Les quatre options sont distinctes et directement soutenues."));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance 1", 1),
                new MealCard("Instance 2", 2)
            }),
            new ToolResults(),
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance 3", 3),
                new MealCard("Instance 4", 4)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 8,
                MaximumSemanticCorrectionTurns = 2,
                MaximumWorkingEvidenceItems = 8,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                SemanticCandidateStrategyEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[]
            {
                "documents.content_cards",
                "documents.content_cards",
                "documents.content_cards"
            },
            executor.ToolNames);
        var recoveryToolSetIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool =>
                tool.Name == "submit_research_action"));
        Assert.True(
            recoveryToolSetIndex >= 0,
            "Tool sets: " + string.Join(
                " || ",
                llm.ToolSets.Select(static toolSet => string.Join(
                    ",",
                    toolSet.Select(static tool => tool.Name)))));
        Assert.Equal(5, recoveryToolSetIndex + 1);
        Assert.Contains(
            llm.ToolSets[recoveryToolSetIndex],
            static tool => tool.Name == "declare_source_insufficiency");
        Assert.Contains(
            llm.ToolSets[recoveryToolSetIndex],
            static tool => tool.Name == "request_user_clarification");
        var recoveryRequest = RequestText(llm, 4);
        Assert.Contains("RECHERCHES_DEJA_EFFECTUEES", recoveryRequest);
        Assert.Contains("nouvelles_preuves=0", recoveryRequest);
        Assert.Contains("rendement=nul", recoveryRequest);
        Assert.Contains("query vide", recoveryRequest);
        Assert.False(executor.Arguments[2].TryGetProperty("q", out _));
        Assert.Equal(
            "representative",
            executor.Arguments[2].GetProperty("inventoryMode").GetString());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.documentary_no_progress.recovery_requested"
            && trace.Fields["consecutive_no_progress_turns"] == "2");
    }

    [Fact]
    public async Task SplitActionWriter_ReopensToolsWhenReviewNeedsMoreEvidenceAfterDuplicateDecision()
    {
        var repeated = Call("search-1", "rag_search", new
        {
            query = "dessert au chocolat facile recette"
        });
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: une proposition sourcee\nDIMENSIONS: aucune matrice\n"
                + "PREUVES_ATOMIQUES: 1 recette\nINTENTIONS_RECHERCHE: dessert chocolat\n"
                + "APPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: une recette directement soutenue\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune recette exploitable"),
            Completion(repeated),
            Completion(repeated with { Id = "search-2" }),
            Completion(repeated with { Id = "search-3" }),
            Completion("PRET_A_REDIGER: E1 est le meilleur candidat actuellement."),
            Completion("Je te propose le premier candidat [E1]."),
            SemanticReview(
                "need_more_evidence",
                "Une recherche differente est necessaire."),
            Completion(Call("search-4", "rag_search", new
            {
                query = "fondant chocolat preparation simple"
            })),
            Completion("PRET_A_REDIGER: E2 apporte la recette directement exploitable."),
            Completion("Je te propose le fondant documente [E2]."),
            SemanticReview("accept", "E2 soutient directement la proposition."));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            SearchResultWithCards(
                "Cuisine/Fondant.pdf",
                "Cuisine",
                "fondant",
                page: 2,
                cardCount: 1));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 9,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Je veux un dessert au chocolat facile, tu proposes quoi ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Empty(llm.ToolSets[4]);
        Assert.Contains(llm.ToolSets[7], static tool => tool.Name == "rag_search");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "need_more_evidence");
    }

    [Fact]
    public async Task NativeLoop_ExecutesSameSearchWhenRetrievalTuningCanChangeTheEvidenceSet()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: valeur prescrite sourcee."),
            Completion(Call("search-1", "rag_search", new
            {
                query = "  Couple   serrage HX-42 ",
                topK = 8,
                maxPerDoc = 5,
                docId = "D1",
                docPath = "Manuals/HydraulicPumpManual.pdf"
            })),
            Completion(Call("search-2", "rag_search", new
            {
                query = "couple serrage hx-42",
                topK = 16,
                maxPerDoc = 1,
                docId = "Manuals/HydraulicPumpManual.pdf"
            })),
            Completion("Le couple prescrit est de 85 NÂ·m [E1]."),
            SemanticReview("accept", "La reponse est complete et soutenue."));
        var executor = new ScriptedToolExecutor(SearchResult(), SearchResult());
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(Intake("Quel est le couple prescrit pour HX-42 ?"), CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.duplicate_rejected");
    }

    [Fact]
    public async Task DuplicateToolCalls_DoNotConsumeTheExternalExecutionBudget()
    {
        var firstCall = Call("search-1", "rag_search", new { query = "option initiale" });
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option complete et sourcee."),
            Completion(firstCall),
            new SourceBackedAgentCompletion(
                string.Empty,
                new[]
                {
                    firstCall with { Id = "search-duplicate" },
                    Call("search-2", "rag_search", new { query = "option complete" })
                },
                "tool_calls",
                220,
                60),
            Completion("L'option complete new-card-1 est disponible [E4]."),
            SemanticReview("accept", "E4 soutient une option complete et utilisable."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Knowledge/Initial.pdf", "Knowledge", "old", 2, 1),
            SearchResultWithCards("Knowledge/Complete.pdf", "Knowledge", "new", 4, 1));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with { MaximumToolCalls = 2 });

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.duplicate_rejected");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.Fields.GetValueOrDefault("error") == "tool_call_budget_exhausted");
    }

    [Fact]
    public async Task CompactState_PrioritizesRecentCanonicalCardsAndPreservesPathAndCategory()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: options sourcees."),
            Completion(Call("old", "rag_search", new { query = "options anciennes" })),
            Completion(Call("new", "rag_search", new { query = "options recentes" })),
            Completion("L'option recente est utilisable [E8]."),
            SemanticReview("accept", "L'option recente est directement soutenue."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Cuisine/Ancien.pdf", "Cuisine", "old", 1, 4),
            SearchResultWithCards("Cuisine/Recent.pdf", "Cuisine", "new", 8, 2));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with { MaximumWorkingEvidenceItems = 2 });

        await runner.RunAsync(Intake("Donne une option documentee."), CancellationToken.None);

        var compactState = RequestText(llm, 3);
        Assert.Contains("Cuisine/Recent.pdf", compactState);
        Assert.Contains("D1 | docPath=Cuisine/Recent.pdf", compactState);
        Assert.Contains("categorie=Cuisine", compactState);
        Assert.Equal(
            1,
            compactState.Split(
                "Cuisine/Recent.pdf",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("new-card-2", compactState);
        Assert.DoesNotContain("Cuisine/Ancien.pdf", compactState);
        Assert.Contains("2 conservees sur 6 citables observees", compactState);
        Assert.Contains("sources visibles distinctes", compactState);
        Assert.Contains("groupe_source=V", compactState);
    }

    [Fact]
    public async Task CompactState_DoesNotHideRecentSearchEvidenceBehindOlderCardInventory()
    {
        var inventoryCall = Call("cards", "documents_content_cards", new
        {
            categoryPath = "Cuisine",
            limit = 40,
            offset = 0
        });
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une proposition sourcee."),
            Completion(inventoryCall),
            Completion(Call("search", "rag_search", new
            {
                query = "dessert chocolat"
            })),
            Completion("Le resultat de recherche recent repond directement [E21]."),
            SemanticReview("accept", "E21 soutient directement la proposition."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(TwentyMealCards()),
                SearchResult()),
            Options() with { MaximumWorkingEvidenceItems = 4 });

        var result = await runner.RunAsync(
            Intake("Propose un dessert au chocolat."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var compactState = RequestText(llm, 3);
        Assert.Contains("E21 | groupe_source=", compactState);
        Assert.Contains("85 N", compactState);
        Assert.Contains("HydraulicPumpManual.pdf", compactState);
        Assert.DoesNotContain("recette-card-1", compactState);
    }

    [Fact]
    public async Task CompactState_RepresentsDistinctSourcesBeforeSameSourceAlternatives()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: une option sourcee\n"
                + "PREUVES_ATOMIQUES: 1 instance\n"
                + "INTENTIONS_RECHERCHE: option\n"
                + "APPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: une option\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune option"),
            Completion(Call("search", "rag_search", new
            {
                query = "options"
            })),
            Completion("L'option la plus recente est documentee [E5]."),
            SemanticReview(
                "accept",
                "E5 soutient directement l'option retenue."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(CompactDiversitySearchResult()),
            Options() with { MaximumWorkingEvidenceItems = 3 });

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var compactState = RequestText(llm, 2);
        Assert.Contains("- E5 |", compactState);
        Assert.Contains("- E2 |", compactState);
        Assert.Contains("- E1 |", compactState);
        Assert.DoesNotContain("- E4 |", compactState);
        Assert.DoesNotContain("- E3 |", compactState);
        Assert.Contains("3 conservees sur 5 citables observees", compactState);
    }

    [Fact]
    public async Task EmergencyCompaction_PreservesUnconsumedPaginationAfterItsOriginalActionAgesOut()
    {
        var llm = new ContextOverflowOnceLlm(
            Completion("Livrable: une option documentee.\nPREUVES_ATOMIQUES: 1 option."),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 5,
                offset = 0
            })),
            Completion(Call("search-1", "rag_search", new { query = "option une" })),
            Completion(Call("search-2", "rag_search", new { query = "option deux" })),
            Completion(Call("search-3", "rag_search", new { query = "option trois" })),
            Completion(Call("search-4", "rag_search", new { query = "option quatre" })),
            new HttpRequestException(
                "request exceeds the available context size",
                null,
                System.Net.HttpStatusCode.BadRequest),
            Completion("L'option Sticks de feta est documentee [E1]."),
            SemanticReview("accept", "E1 soutient directement l'option."));
        var executor = new ScriptedToolExecutor(
            PagedContentCardInventoryResult(),
            SearchResult(),
            SearchResult(),
            SearchResult(),
            SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumContextTokens = 4096,
                MaximumTurns = 9
            });

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var recoveredRequest = RequestText(llm, 7);
        Assert.Contains("PAGINATIONS ENCORE DISPONIBLES", recoveredRequest);
        Assert.Contains("documents.content_cards", recoveredRequest);
        Assert.Contains("prochain_offset_exact=5", recoveredRequest);
    }

    [Fact]
    public async Task DuplicateOnlyTurn_CompactsItsRepairGuidanceWithEarlierSemanticFeedback()
    {
        var firstCall = Call("search-1", "rag_search", new { query = "option initiale" });
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option documentee."),
            Completion(firstCall),
            Completion(firstCall with { Id = "search-duplicate" }),
            Completion("L'option documentee est disponible [E1]."),
            SemanticReview("accept", "E1 soutient directement l'option."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with { MaximumContextTokens = 4096 });

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            "duplicate_tool_call",
            RequestText(llm, 3));
        Assert.Contains("ACTIONS DEJA EXECUTEES", RequestText(llm, 3));
    }

    [Fact]
    public async Task NativeLoop_PreservesTheLlmChosenOffsetWhenTheRouteChanges()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option documentee.\nPREUVES_ATOMIQUES: 1 option."),
            Completion(Call("cards-first", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 5,
                offset = 0
            })),
            Completion(Call("cards-new-route", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                docPath = "Cuisine/recettes-indexees.pdf",
                limit = 30,
                offset = 15
            })),
            Completion("L'option Sticks de feta est documentee [E1]."),
            SemanticReview("accept", "E1 soutient directement l'option."));
        var executor = new ScriptedToolExecutor(
            PagedContentCardInventoryResult(),
            ContentCardInventoryResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with { MaximumTurns = 8 });

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "documents.content_cards", "documents.content_cards" },
            executor.ToolNames);
        Assert.Equal(15, executor.Arguments[1].GetProperty("offset").GetInt32());
        Assert.Equal(
            "Cuisine/recettes-indexees.pdf",
            executor.Arguments[1].GetProperty("docPath").GetString());
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.mechanical_adjustment"
            && trace.Fields.GetValueOrDefault("adjustment")
                == "pagination_offset_reset_to_new_route:15");
    }

    [Fact]
    public async Task UnifiedSearch_RoutesAnLlmQueryArrayToTheMultiSearchBackend()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: deux options sourcees. Rechercher les deux facettes ensemble."),
            Completion(Call("search-many", "rag_search", new
            {
                queries = new[] { "couple serrage HX-42", "boulons couvercle HX-42" },
                topK = 8
            })),
            Completion("Le couple prescrit est de 85 NÂ·m [E1]."),
            SemanticReview("accept", "La reponse est complete et soutenue."));
        var executor = new ScriptedToolExecutor(SearchResult("rag.multi_search"));
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Quel est le couple prescrit pour les boulons du couvercle HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { "rag.multi_search" }, executor.ToolNames);
        var retrievalPlan = Assert.IsType<RetrievalPlan>(result.RetrievalPlan);
        Assert.Equal("rag.multi_search", Assert.Single(retrievalPlan.Requests).ToolName);
    }

    [Fact]
    public async Task NativeLoop_ExecutesIndependentToolCallsFromOneTurnInParallel()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: valeur sourcee. Rechercher deux formulations independantes."),
            new SourceBackedAgentCompletion(
                string.Empty,
                new[]
                {
                    Call("search-a", "rag_search", new { query = "couple serrage HX-42" }),
                    Call("search-b", "rag_search", new { query = "boulons couvercle HX-42" })
                },
                "tool_calls",
                100,
                30),
            Completion("Le couple prescrit est de 85 NÂ·m [E1]."),
            SemanticReview("accept", "La reponse est complete et soutenue."));
        var executor = new ConcurrentToolExecutor();
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Quel est le couple prescrit pour les boulons du couvercle HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.MaximumConcurrency);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.parallel_batch.started");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.parallel_batch.completed");
    }

    [Fact]
    public async Task NativeLoop_ReservesTheFinalTurnAndRunsASeparateToolFreeSemanticJudge()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: valeur technique sourcee."),
            Completion(Call("search-1", "rag_search", new { query = "couple serrage HX-42" })),
            Completion("Le couple prescrit est de 85 NÂ·m [E1]."),
            SemanticReview("accept", "La valeur est directement soutenue."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with { MaximumTurns = 2 });

        var result = await runner.RunAsync(
            Intake("Quel est le couple prescrit pour HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.NotEmpty(llm.ToolSets[1]);
        Assert.Empty(llm.ToolSets[2]);
        AssertSemanticReviewTool(llm.ToolSets[3]);
        Assert.Contains("budget d'exploration est termine", RequestText(llm, 2));
    }

    [Fact]
    public async Task NativeLoop_UsesPenultimateTurnForDraftAndKeepsFinalTurnForMechanicalRepair()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: valeur technique sourcee."),
            Completion(Call("search-1", "rag_search", new { query = "couple serrage HX-42" })),
            Completion("Le couple prescrit est de 85 NÃ‚Â·m [E999]."),
            Completion("Le couple prescrit est de 85 NÃ‚Â·m [E1]."),
            SemanticReview("accept", "La valeur corrigee est directement soutenue."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with { MaximumTurns = 3 });

        var result = await runner.RunAsync(
            Intake("Quel est le couple prescrit pour HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("[E1]", result.Answer);
        Assert.NotEmpty(llm.ToolSets[1]);
        Assert.Empty(llm.ToolSets[2]);
        Assert.Empty(llm.ToolSets[3]);
        AssertSemanticReviewTool(llm.ToolSets[4]);
        Assert.Equal(
            new[] { false, true },
            result.TraceEvents
                .Where(static trace => trace.EventName == "source_backed_agent_v2.answer.verified")
                .Select(static trace => trace.Fields["valid"] == "true")
                .ToArray());
    }

    [Fact]
    public async Task NativeLoop_CanApplyTwoSemanticRevisionsWithoutTakingSemanticOwnershipInCode()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: option complete et sourcee."),
            Completion(Call("search-1", "rag_search", new { query = "option complete" })),
            Completion("Premiere formulation [E1]."),
            SemanticReview("revise", "La formulation reste vague."),
            Completion("Deuxieme formulation plus concrete [E1]."),
            SemanticReview("revise", "La formulation doit etre directement utilisable."),
            Completion("Option HX-42 complete et directement utilisable [E1]."),
            SemanticReview("accept", "Le livrable est complet et directement soutenu."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with { MaximumTurns = 4 });

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("directement utilisable", result.Answer);
        Assert.Equal(
            new[] { "revise", "revise", "accept" },
            result.TraceEvents
                .Where(static trace => trace.EventName == "source_backed_agent_v2.semantic_review.completed")
                .Select(static trace => trace.Fields["decision"])
                .ToArray());
        Assert.NotEmpty(llm.ToolSets[1]);
        Assert.All(llm.ToolSets.Skip(2), static toolSet =>
        {
            if (toolSet.Count == 0)
                return;
            AssertSemanticReviewTool(toolSet);
        });
    }

    [Fact]
    public async Task SplitActionWriter_ReturnsSemanticRevisionToTheOrchestratorBeforeRewriting()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: valeur technique sourcee."),
            Completion(Call("search-1", "rag_search", new { query = "couple serrage HX-42" })),
            Completion("PRET_A_REDIGER: la preuve technique est disponible."),
            Completion("Le premier brouillon reste vague [E1]."),
            SemanticReview("revise", "Employer la valeur exacte directement soutenue."),
            Completion("PRET_A_REDIGER: employer la valeur exacte de E1."),
            Completion("Le couple prescrit est de 85 NÂ·m [E1]."),
            SemanticReview("accept", "La valeur exacte est directement soutenue."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                MaximumTurns = 6,
                MaximumActionTokens = 320,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Quel est le couple prescrit pour HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("85 NÂ·m [E1]", result.Answer);
        Assert.NotEmpty(llm.ToolSets[2]);
        Assert.Empty(llm.ToolSets[3]);
        Assert.NotEmpty(llm.ToolSets[5]);
        Assert.Empty(llm.ToolSets[6]);
        Assert.Equal(320, llm.MaxTokens[2]);
        Assert.Equal(900, llm.MaxTokens[3]);
        Assert.Contains("DECISION D'ORCHESTRATION UNIQUEMENT", RequestText(llm, 2));
        Assert.Contains("REDACTION FINALE SEPAREE", RequestText(llm, 3));
        Assert.Contains("redacteur final source-backed", RequestText(llm, 3));
        Assert.Contains("REVUE SEMANTIQUE INDEPENDANTE", RequestText(llm, 5));
        Assert.Contains("DECISION D'ORCHESTRATION UNIQUEMENT", RequestText(llm, 5));
        Assert.Contains("REDACTION FINALE SEPAREE", RequestText(llm, 6));
        Assert.Equal(
            new[] { "false", "false" },
            result.TraceEvents
                .Where(static trace => trace.EventName == "source_backed_agent_v2.writer.completed")
            .Select(static trace => trace.Fields["direct_revision"])
            .ToArray());
    }

    [Fact]
    public async Task SplitActionWriter_ReservesFollowUpAfterProductiveFinalTurnRetrieval()
    {
        var approvedIds = new[] { "E1", "E2" };
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: grille sourcee\nDIMENSIONS: 2 x 1\n"
                + "PREUVES_ATOMIQUES: 2 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 2 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 2 instances"),
            Completion(Call("cards-final", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 2,
                offset = 0
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-final",
                approvedIds,
                Array.Empty<string>())),
            Completion(Call(
                "selection-final",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = approvedIds,
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            SemanticReview(
                "accept",
                "Les deux valeurs retenues sont completes, distinctes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Option documentee A", 1),
                new MealCard("Option documentee B", 2)
            })),
            Options() with
            {
                MaximumTurns = 1,
                MaximumSemanticCorrectionTurns = 0,
                MaximumSelectionProtocolRepairTurns = 2,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de deux options documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            approvedIds,
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.productive_retrieval.follow_up_reserved"
            && trace.Fields["turn"] == "1"
            && trace.Fields["new_evidence_count"] == "2"
            && trace.Fields["previous_maximum_run_turns"] == "1"
            && trace.Fields["maximum_run_turns"] == "3"
            && trace.Fields["audit_turn"] == "2"
            && trace.Fields["writer_turn"] == "3"
            && trace.Fields["decision_source"]
                == "mechanical_lifecycle_reservation");
    }

    [Fact]
    public async Task SplitActionWriter_RejectsStoppingAfterNavigationWithoutCitableEvidence()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: reponse sourcee."),
            Completion(Call("nav-1", "documents_navigation", new { categoryPath = "Manuals" })),
            Completion("PRET_A_REDIGER: aucune preuve n'est disponible."),
            Completion(Call("search-1", "rag_search", new { query = "couple serrage HX-42" })),
            Completion("PRET_A_REDIGER: la preuve technique est disponible."),
            Completion("Le couple prescrit est de 85 NÂ·m [E2]."),
            SemanticReview("accept", "La valeur exacte est directement soutenue."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(NavigationResult(), SearchResult()),
            Options() with
            {
                MaximumTurns = 6,
                MaximumActionTokens = 320,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Quel est le couple prescrit pour HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("85 NÂ·m [E2]", result.Answer);
        Assert.Contains(
            "DECISION D'ARRET REFUSEE PAR LE CONTRAT MECANIQUE",
            RequestText(llm, 3));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.action.no_citable_evidence_rejected");
    }

    [Fact]
    public async Task SplitActionWriter_DuplicateNavigationCannotInviteAnUncitedAnswer()
    {
        var navigation = Call(
            "nav-1",
            "documents_navigation",
            new { categoryPath = "Manuals" });
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: reponse sourcee."),
            Completion(navigation),
            Completion(navigation with { Id = "nav-2" }),
            Completion(Call("search-1", "rag_search", new { query = "couple serrage HX-42" })),
            Completion("PRET_A_REDIGER: la preuve technique est disponible."),
            Completion("Le couple prescrit est de 85 NÃ‚Â·m [E2]."),
            SemanticReview("accept", "La valeur exacte est directement soutenue."));
        var executor = new ScriptedToolExecutor(NavigationResult(), SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumActionTokens = 320,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Quel est le couple prescrit pour HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "documents.navigation", "rag.search" },
            executor.ToolNames);
        Assert.Contains("PRET_A_REDIGER est invalide", RequestText(llm, 3));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.duplicate_rejected");
    }

    [Fact]
    public async Task SplitActionWriter_EnforcesTheAtomicCountAuthoredByTheLlmPlan()
    {
        var cards = TwentyMealCards();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("search-1", "rag_search", new { query = "recettes nommees" })),
            Completion("PRET_A_REDIGER: quatre recettes sont visibles."),
            Completion(Call("cards-20", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20
            })),
            Completion("PRET_A_REDIGER: vingt-quatre recettes sont visibles."),
            Completion(SelectionCall(
                "selection-20",
                Enumerable.Range(5, 20)
                    .Select(static index => $"E{index}")
                    .ToArray())),
            SemanticReview(
                "accept",
                "Les vingt valeurs selectionnees sont distinctes et directement soutenues."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Cuisine/recettes.pdf", "Cuisine", "initial", 4, 4),
            ContentCardInventoryResult(cards));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumActionTokens = 320,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.DoesNotContain("[E1]", result.Answer);
        Assert.Equal(20, SourceContractVerifier.ExtractEvidenceIds(result.Answer).Count);
        Assert.Equal(
            new[] { "rag.search", "documents.content_cards" },
            executor.ToolNames);
        Assert.Contains("CONTRAT DU PLAN LLM", RequestText(llm, 3));
        Assert.Contains("20 preuves atomiques", RequestText(llm, 3));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.action.atomic_evidence_count_rejected"
            && trace.Fields["required"] == "20"
            && trace.Fields["observed"] == "4");
        Assert.Contains(
            "CONTRAT MECANIQUE DE TA SELECTION",
            RequestText(llm, 5));
        Assert.Equal(2, llm.Requests[5].Count);
        Assert.Contains(
            "CONTEXTE COMPACT DE SELECTION FINALE",
            RequestText(llm, 5),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "CANDIDATS CITABLES, SANS ORDRE DE PREFERENCE",
            RequestText(llm, 5),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "| preuve=",
            RequestText(llm, 5),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            RequestText(llm, 5).Length < 12000,
            $"Selection prompt has {RequestText(llm, 5).Length} characters.");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_rejected"
            && trace.Fields["required"] == "20"
            && trace.Fields["declared"] == "0");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.structured_renderer.completed"
            && trace.Fields["rows"] == "5"
            && trace.Fields["columns"] == "4"
            && trace.Fields["evidence_ids"] == "20");
        var selectionTool = Assert.Single(
            llm.ToolSets[5],
            static tool => tool.Name == "submit_evidence_selection");
        var selectionProperties = selectionTool.Parameters
            .GetProperty("properties");
        var evidenceIdsSchema = selectionProperties
            .GetProperty("evidenceIds");
        var layoutProperties = selectionProperties
            .GetProperty("layout")
            .GetProperty("properties");
        Assert.Equal(20, evidenceIdsSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(20, evidenceIdsSchema.GetProperty("maxItems").GetInt32());
        Assert.True(evidenceIdsSchema.GetProperty("uniqueItems").GetBoolean());
        Assert.Equal(
            4,
            layoutProperties.GetProperty("columns").GetProperty("minItems").GetInt32());
        Assert.Equal(
            5,
            layoutProperties.GetProperty("rows").GetProperty("minItems").GetInt32());
        Assert.True(
            layoutProperties.GetProperty("rows").GetProperty("uniqueItems").GetBoolean());
    }

    [Fact]
    public async Task SplitActionWriter_LetsTheLlmStopBelowAProvisionalFlatMultiItemTarget()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: options documentees\nDIMENSIONS: aucune grille imposee\n"
                + "PREUVES_ATOMIQUES: 5 options nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: options pertinentes\n"
                + "APPROCHE_OUTILS: recherche puis decision semantique\n"
                + "ACCEPTER_SI: assez d'options pour repondre utilement\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune option pertinente"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                inventoryMode = "representative",
                limit = 3
            })),
            Completion(CandidateAuditCall(
                "audit",
                new[] { "E1", "E2" },
                new[] { "E3" })),
            Completion(Call(
                "adaptive-checkpoint",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1", "E2" },
                    reason = "Ces deux options repondent utilement a la demande ouverte."
                })),
            Completion("Option documentee A [E1]\nOption documentee B [E2]"),
            SemanticReview("accept", "Les deux options nommées sont documentées par E1 et E2."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Option documentee A", 1),
                new MealCard("Option documentee B", 2),
                new MealCard("Rubrique generique", 3)
            })),
            Options() with
            {
                MaximumTurns = 5,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Propose-moi quelques options documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "E1", "E2" },
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        Assert.Contains(
            "NOMBRE_DE_PREUVES_SELECTIONNEES: 2",
            RequestText(llm, llm.Requests.Count - 2));
        Assert.Contains(
            "n'omets aucune unite selectionnee",
            RequestText(llm, llm.Requests.Count - 2));
        var adaptiveSelectionIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool =>
                tool.Name == "submit_flat_evidence_selection"));
        Assert.True(adaptiveSelectionIndex >= 0);
        Assert.Contains(
            "PREUVES APPROUVEES DISPONIBLES",
            RequestText(llm, adaptiveSelectionIndex));
        Assert.Contains(
            "E1 | Option documentee A",
            RequestText(llm, adaptiveSelectionIndex));
        Assert.Contains(
            "E2 | Option documentee B",
            RequestText(llm, adaptiveSelectionIndex));
        var adaptiveSelectionTool = Assert.Single(
            llm.ToolSets[adaptiveSelectionIndex],
            static tool => tool.Name == "submit_flat_evidence_selection");
        var adequacyToolSet = llm.ToolSets[adaptiveSelectionIndex];
        Assert.Contains(
            adequacyToolSet,
            static tool => tool.Name == "submit_flat_evidence_selection");
        Assert.Contains(
            adequacyToolSet,
            static tool => tool.Name == "submit_flat_evidence_gap");
        Assert.Contains(
            adequacyToolSet,
            static tool => tool.Name == "submit_flat_evidence_clarification");
        var evidenceIdsSchema = adaptiveSelectionTool.Parameters
            .GetProperty("properties")
            .GetProperty("evidenceIds");
        Assert.Contains(
            "toutes les composantes explicitement demandees",
            RequestText(llm, adaptiveSelectionIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, evidenceIdsSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(2, evidenceIdsSchema.GetProperty("maxItems").GetInt32());
        Assert.Contains(
            result.TraceEvents,
            static trace =>
                trace.EventName
                    == "source_backed_agent_v2.action.semantic_selection_accepted"
                && trace.Fields["selected_evidence"] == "E1,E2"
                && trace.Fields["count_mode"] == "adaptive_flat_target");
        Assert.DoesNotContain(
            result.TraceEvents,
            static trace =>
                trace.EventName
                    == "source_backed_agent_v2.action.semantic_selection_rejected");
    }

    [Fact]
    public async Task SplitActionWriter_FlatAdequacyCanExpandItsChosenEvidenceContext()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "pompe HX-42 exigences",
            topK = 4
        });
        var intake = Intake("Résume les deux exigences documentées de la pompe HX-42.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "deux exigences documentées",
                    structuredLayout = false,
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigences documentées",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "audit-search",
                new[] { "E1" },
                Array.Empty<string>())),
            Completion(Call(
                "flat-context-gap",
                "submit_flat_evidence_context_gap",
                new
                {
                    evidenceId = "E1",
                    missingRequirements = new[]
                    {
                        "La seconde exigence documentée du même équipement"
                    },
                    reason =
                        "Le passage visible localise le bon équipement et son contexte peut contenir l'exigence complémentaire."
                })),
            Completion(CandidateAuditCall(
                "audit-context",
                new[] { "E2" },
                Array.Empty<string>())),
            Completion(Call(
                "flat-selection",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1", "E2" }
                })),
            Completion(
                "Le serrage prescrit est de 85 N·m [E1]. "
                + "Le contrôle complémentaire doit être consigné [E2]."),
            SemanticReview("accept", "E1 décrit le serrage et E2 la consignation du contrôle."));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            Results(
                "documents.context",
                new
                {
                    document = new
                    {
                        docId = "doc-hx42",
                        docName = "HydraulicPumpManual.pdf",
                        docPath = "Manuals/HydraulicPumpManual.pdf"
                    },
                    items = new[]
                    {
                        new
                        {
                            docId = "doc-hx42",
                            docName = "HydraulicPumpManual.pdf",
                            docPath = "Manuals/HydraulicPumpManual.pdf",
                            pageStart = 43,
                            pageEnd = 43,
                            chunkId = "doc-hx42:43:1",
                            text =
                                "Après serrage, consigner le contrôle complémentaire dans le registre de maintenance."
                        }
                    }
                }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 7,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                SemanticCandidateStrategyEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "documents.context" },
            executor.ToolNames);
        var adequacyToolSetIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool =>
                tool.Name == "submit_flat_evidence_context_gap"));
        Assert.True(adequacyToolSetIndex >= 0);
        var contextGapTool = Assert.Single(
            llm.ToolSets[adequacyToolSetIndex],
            static tool => tool.Name == "submit_flat_evidence_context_gap");
        Assert.Equal(
            new[] { "E1" },
            contextGapTool.Parameters
                .GetProperty("properties")
                .GetProperty("evidenceId")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        var contextArguments = executor.Arguments[1];
        Assert.Equal("doc-hx42", contextArguments
            .GetProperty("docId").GetString());
        Assert.Equal("Manuals/HydraulicPumpManual.pdf", contextArguments
            .GetProperty("docPath").GetString());
        Assert.Equal("doc-hx42:42:3", contextArguments
            .GetProperty("chunkId").GetString());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.flat_evidence_adequacy.completed"
            && trace.Fields["decision"] == "expand_context"
            && trace.Fields["approved_evidence_ids"] == "E1");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.research_transition.strategy_completed");
    }

    [Fact]
    public async Task SplitActionWriter_FlatAdequacyRejectsUnpublishedContextEvidenceId()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "pompe HX-42 exigences",
            topK = 4
        });
        var intake = Intake("Résume les deux exigences documentées de la pompe HX-42.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "deux exigences documentées",
                    structuredLayout = false,
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigences documentées",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "audit-search",
                new[] { "E1" },
                Array.Empty<string>())),
            Completion(Call(
                "flat-context-gap-invalid",
                "submit_flat_evidence_context_gap",
                new
                {
                    evidenceId = "E999",
                    missingRequirements = new[]
                    {
                        "Une exigence documentaire complémentaire"
                    },
                    reason = "La preuve inventée prétend localiser le bon document."
                })),
            Completion(Call(
                "fallback-search",
                "start_document_search",
                new
                {
                    query = "exigence complémentaire HX-42",
                    limit = 4
                })));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            new ToolResults());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 3,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                SemanticCandidateStrategyEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.DoesNotContain("documents.context", executor.ToolNames);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.flat_evidence_adequacy.completed"
            && trace.Fields["decision"] == "continue"
            && trace.Fields["protocol_valid"] == "false"
            && trace.Fields["reason"]
                == "Le checkpoint de contexte n'a pas respecte son contrat.");
    }

    [Fact]
    public async Task SplitActionWriter_FlatAdequacyCheckpointCanClarifyAfterObservation()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: options documentees\nDIMENSIONS: aucune grille imposee\n"
                + "PREUVES_ATOMIQUES: 3 options nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: options pertinentes\n"
                + "APPROCHE_OUTILS: recherche puis decision semantique\n"
                + "ACCEPTER_SI: assez d'options pour repondre utilement\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune option pertinente"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                inventoryMode = "representative",
                limit = 2
            })),
            Completion(CandidateAuditCall(
                "audit",
                new[] { "E1", "E2" },
                Array.Empty<string>())),
            Completion(Call(
                "clarification-checkpoint",
                "submit_flat_evidence_clarification",
                new
                {
                    understanding = "Plusieurs options documentees correspondent a la demande.",
                    options = new[]
                    {
                        "Comparer les options",
                        "Choisir une option precise"
                    },
                    executionImpact =
                        "La reponse determinera si le livrable compare ou detaille une option.",
                    ambiguityKind = "deliverable",
                    reason = "Seul l'utilisateur peut choisir la forme du livrable."
                })));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Option documentee A", 1),
                new MealCard("Option documentee B", 2)
            })),
            Options() with
            {
                MaximumTurns = 5,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne-moi les options documentees."),
            CancellationToken.None);

        var clarification = Assert.IsType<SourceBackedClarificationDecision>(
            result.Clarification);
        Assert.Equal("clarify", result.JudgeDecision.Decision);
        Assert.Equal(2, clarification.Options.Count);
        Assert.Contains("Comparer les options", clarification.Options);
        Assert.Null(result.FinalDraft);
        Assert.Contains(
            result.TraceEvents,
            static trace =>
                trace.EventName
                    == "source_backed_agent_v2.flat_evidence_adequacy.completed"
                && trace.Fields["decision"] == "clarify"
                && trace.Fields["protocol_valid"] == "true");
        Assert.Contains(
            result.TraceEvents,
            static trace =>
                trace.EventName
                    == "source_backed_agent_v2.clarification.requested"
                && trace.Fields["decision_source"] == "llm_orchestrator");
    }

    [Fact]
    public async Task SplitActionWriter_UsesTheLlmWorkspaceDecisionAsTheFinalWideSelection()
    {
        var selectedIds = new[] { "E1", "E2" };
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: deux options sourcees\nDIMENSIONS: deux options\n"
                + "PREUVES_ATOMIQUES: 2 options nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: options documentees\n"
                + "APPROCHE_OUTILS: inventaire puis decision\n"
                + "ACCEPTER_SI: deux options distinctes\n"
                + "INSUFFISANT_SEULEMENT_SI: moins de deux options"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 1,
                offset = 0
            })),
            Completion(Call("cards-next", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 1,
                offset = 5
            })),
            Completion(Call("workspace-ready", "manage_evidence_workspace", new
            {
                status = "ready_to_write",
                retainEvidenceIds = selectedIds,
                note = "Ces deux options nommees satisfont la demande."
            })),
            Completion("Option documentee A [E1]\nOption documentee B [E2]"),
            SemanticReview(
                "accept",
                "Les deux options distinctes sont directement soutenues."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                PagedContentCardInventoryResult(),
                ContentCardInventoryResult(new[]
                {
                    new MealCard("Option documentee B", 2)
                })),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne deux options documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            selectedIds,
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        Assert.Equal(6, llm.Requests.Count);
        Assert.DoesNotContain(
            llm.ToolSets[2],
            static tool => tool.Name == "manage_evidence_workspace");
        var workspaceTool = Assert.Single(
            llm.ToolSets[3],
            static tool => tool.Name == "manage_evidence_workspace");
        Assert.Contains(
            "ready_to_write",
            workspaceTool.Parameters.GetRawText(),
            StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
            == "source_backed_agent_v2.evidence_workspace.ready_to_write_accepted"
            && trace.Fields["selected_evidence_ids"] == "E1,E2");
    }

    [Fact]
    public async Task GlobalStructuredAssignment_ReopensOnlyForMechanicallyMissingAlternatives()
    {
        var initialAction = Call(
            "initial-cards",
            "documents_content_cards",
            new
            {
                categoryPath = "Knowledge",
                limit = 5,
                offset = 0
            });
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialAction.Id,
                    initialAction.Name,
                    initialAction.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "options documentees",
                    initialCapability = "documents_content_cards",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var firstDraft = """
            | Periode | Option A | Option B |
            |---|---|---|
            | Premiere | Instance A1 [E1] | Instance B1 [E2] |
            | Deuxieme | Instance A2 [E3] | Instance B2 [E4] |
            """;
        var llm = new ScriptedAgentLlm(
            Completion(firstDraft),
            Completion("C01=ACCEPT\nC02=REJECT:option incompatible"),
            Completion("C03=ACCEPT\nC04=REJECT:option incomplete"),
            Completion(Call("more-cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 1,
                offset = 5
            })),
            Completion("""{"c02":"E5","c04":"E6"}"""),
            Completion("C02=ACCEPT"),
            Completion("C04=ACCEPT"));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance A1", 1),
                new MealCard("Instance B1", 2),
                new MealCard("Instance A2", 3),
                new MealCard("Instance B2", 4),
                new MealCard("Alternative locale A", 5)
            }),
            ContentCardInventoryResult(new[]
            {
                new MealCard("Alternative neuve B", 6)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 5,
                MaximumSemanticCorrectionTurns = 4,
                MaximumWorkingEvidenceItems = 8,
                MaximumFlatStructuredSelectionItems = 2,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = false,
                SemanticCandidateStrategyEnabled = false,
                SemanticColumnRoleReviewEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(
            result.IsSourceVerified,
            "Answer=" + result.Answer
            + " | Errors=" + string.Join(
                " | ",
                result.Verification?.Errors.Select(static error =>
                    error.Code + ":" + error.Message)
                ?? Array.Empty<string>())
            + " | Trace=" + string.Join(
                " || ",
                result.TraceEvents.TakeLast(8).Select(static trace =>
                    trace.EventName + "{" + string.Join(
                        ";",
                        trace.Fields.Select(static field =>
                            field.Key + "=" + field.Value)) + "}"))
            + " | Contracts=" + string.Join(
                " || ",
                llm.StructuredOutputContracts.Select(static contract =>
                    contract.Name + ":" + contract.Schema.GetRawText())));
        Assert.Equal(2, executor.ToolNames.Count);
        var selectedAlternativeIds = SourceContractVerifier
            .ExtractEvidenceIds(result.Answer)
            .Where(static id => id is "E5" or "E6")
            .ToArray();
        Assert.Equal(2, selectedAlternativeIds.Length);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.structured_assignment_revision.collection_reopened"
            && trace.Fields["rejected_cells"] == "2"
            && trace.Fields["unselected_candidates"] == "1"
            && trace.Fields["missing_alternatives"] == "1"
            && trace.Fields["candidate_pool_target"] == "6"
            && trace.Fields["decision_source"]
                == "llm_rejected_assignments_lack_local_alternatives");
        var revisionContract = Assert.Single(
            llm.StructuredOutputContracts,
            static contract => contract.Name
                == "source_backed_structured_assignment_revision_v2");
        Assert.DoesNotContain(
            "E2",
            revisionContract.Schema
                .GetProperty("properties")
                .GetProperty("c02")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static value => value.GetString()));
        Assert.DoesNotContain(
            "E4",
            revisionContract.Schema
                .GetProperty("properties")
                .GetProperty("c04")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static value => value.GetString()));
    }

    [Fact]
    public async Task GlobalStructuredAssignment_UsesLocalLlmRevisionWhenFreshCollectionCannotFinish()
    {
        var initialAction = Call(
            "initial-cards",
            "documents_content_cards",
            new
            {
                categoryPath = "Knowledge",
                limit = 6,
                offset = 0
            });
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialAction.Id,
                    initialAction.Name,
                    initialAction.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "options documentees",
                    initialCapability = "documents_content_cards",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion("""
                | Periode | Option A | Option B |
                |---|---|---|
                | Premiere | Instance A1 [E1] | Instance B1 [E2] |
                | Deuxieme | Instance A2 [E3] | Instance B2 [E4] |
                """),
            Completion("C01=ACCEPT\nC02=REJECT:option incompatible"),
            Completion("C03=ACCEPT\nC04=REJECT:option incomplete"),
            Completion("""{"c02":"E5","c04":"E6"}"""),
            Completion("C02=ACCEPT"),
            Completion("C04=ACCEPT"));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance A1", 1),
                new MealCard("Instance B1", 2),
                new MealCard("Instance A2", 3),
                new MealCard("Instance B2", 4),
                new MealCard("Alternative locale A", 5),
                new MealCard("Alternative locale B", 6)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 3,
                MaximumSemanticCorrectionTurns = 0,
                MaximumWorkingEvidenceItems = 8,
                MaximumFlatStructuredSelectionItems = 2,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = false,
                SemanticCandidateStrategyEnabled = false,
                SemanticColumnRoleReviewEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Single(executor.ToolNames);
        Assert.Contains("Alternative locale A [E5]", result.Answer);
        Assert.Contains("Alternative locale B [E6]", result.Answer);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.structured_assignment_revision.collection_reopened");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.structured_assignment_revision.path_decided"
            && trace.Fields["requires_collection"] == "false"
            && trace.Fields["fresh_evidence_lifecycle_available"] == "false"
            && trace.Fields["decision_source"]
                == "llm_rejected_assignments_have_local_alternatives");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.structured_assignment_revision.patch_completed"
            && trace.Fields["revised_cells"] == "2");
    }

    [Fact]
    public async Task GlobalStructuredAssignment_LetsLlmReopenResearchAfterRepeatedRejections()
    {
        var initialAction = Call(
            "initial-cards",
            "documents_content_cards",
            new
            {
                categoryPath = "Knowledge",
                limit = 6,
                offset = 0
            });
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialAction.Id,
                    initialAction.Name,
                    initialAction.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "options documentees",
                    initialCapability = "documents_content_cards",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion("""
                | Periode | Option A | Option B |
                |---|---|---|
                | Premiere | Instance A1 [E1] | Instance B1 [E2] |
                | Deuxieme | Instance A2 [E3] | Instance B2 [E4] |
                """),
            Completion("C01=ACCEPT\nC02=REJECT:option incompatible"),
            Completion("C03=ACCEPT\nC04=REJECT:option incomplete"),
            Completion("""{"c02":"E5","c04":"E6"}"""),
            Completion("C02=REJECT:alternative encore incompatible"),
            Completion("C04=REJECT:alternative encore incomplete"),
            Completion("""
                {"action":"collect_more"}
                """),
            Completion(Call("more-cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 2,
                offset = 6
            })),
            Completion("""{"c02":"E7","c04":"E8"}"""),
            Completion("C02=ACCEPT"),
            Completion("C04=ACCEPT"));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance A1", 1),
                new MealCard("Instance B1", 2),
                new MealCard("Instance A2", 3),
                new MealCard("Instance B2", 4),
                new MealCard("Alternative locale A", 5),
                new MealCard("Alternative locale B", 6)
            }),
            ContentCardInventoryResult(new[]
            {
                new MealCard("Alternative neuve A", 7),
                new MealCard("Alternative neuve B", 8)
            }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 5,
                MaximumSemanticCorrectionTurns = 4,
                MaximumWorkingEvidenceItems = 10,
                MaximumFlatStructuredSelectionItems = 2,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = false,
                SemanticCandidateStrategyEnabled = false,
                SemanticColumnRoleReviewEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains("Alternative neuve A [E7]", result.Answer);
        Assert.Contains("Alternative neuve B [E8]", result.Answer);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.structured_assignment_revision.continuation_decided"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["action"] == "collect_more_evidence"
            && trace.Fields["missing_cells"] == "2"
            && trace.Fields["decision_source"] == "llm_orchestrator");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.structured_assignment_revision.collection_reopened"
            && trace.Fields["decision_source"]
                == "llm_repeated_rejections_require_fresh_evidence"
            && trace.Fields["candidate_pool_target"] == "8");
        Assert.Contains(
            llm.StructuredOutputContracts,
            static contract => contract.Name
                == "source_backed_structured_assignment_continuation_v3");
    }

    [Fact]
    public async Task GlobalStructuredAssignment_AuditsEveryCandidateBeforeWriterSelection()
    {
        var initialAction = Call(
            "initial-cards",
            "documents_content_cards",
            new
            {
                categoryPath = "Knowledge",
                limit = 6,
                offset = 0
            });
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialAction.Id,
                    initialAction.Name,
                    initialAction.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "options documentees",
                    initialCapability = "documents_content_cards",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var draft = """
            | Periode | Option A | Option B |
            |---|---|---|
            | Premiere | Instance A1 [E1] | Instance B1 [E2] |
            | Deuxieme | Instance A2 [E3] | Instance B2 [E4] |
            """;
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "candidate-audit",
                new[] { "E1", "E2", "E3", "E4" },
                new[] { "E5", "E6" })),
            Completion(draft),
            Completion("C01=ACCEPT\nC02=ACCEPT"),
            Completion("C03=ACCEPT\nC04=ACCEPT"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Instance A1", 1),
                new MealCard("Instance B1", 2),
                new MealCard("Instance A2", 3),
                new MealCard("Instance B2", 4),
                new MealCard("Rubrique generale", 5),
                new MealCard("Instruction incomplete", 6)
            })),
            Options() with
            {
                MaximumTurns = 4,
                MaximumSemanticCorrectionTurns = 2,
                MaximumWorkingEvidenceItems = 8,
                MaximumFlatStructuredSelectionItems = 2,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                SemanticCandidateStrategyEnabled = false,
                SemanticColumnRoleReviewEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "E1", "E2", "E3", "E4" },
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["candidates"] == "6"
            && trace.Fields["approved_evidence_ids"] == "E1,E2,E3,E4"
            && trace.Fields["rejected_evidence_ids"] == "E5,E6"
            && trace.Fields["approved_sources"] == "4"
            && trace.Fields["collection_open"] == "false");
        var auditRequest = RequestText(llm, 0);
        Assert.Contains("TYPE ATOMIQUE ATTENDU", auditRequest);
        Assert.Contains("Instance A1", auditRequest);
        Assert.Contains("Instance B2", auditRequest);
        Assert.Contains("Rubrique generale", auditRequest);
        Assert.Contains("Instruction incomplete", auditRequest);
        Assert.Contains("titre canonique exact", auditRequest);
        Assert.Contains("son placement sera juge plus tard", auditRequest);
        Assert.DoesNotContain("AXES DE PLACEMENT", auditRequest);
        Assert.DoesNotContain("E5 |", RequestText(llm, 1));
        Assert.DoesNotContain("E6 |", RequestText(llm, 1));
    }

    [Fact]
    public void StructuredColumnCoverage_UsesOnlyLlmAuthoredCompatibilityForMatching()
    {
        var rawBundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance 1", 1),
                new MealCard("Instance 2", 2),
                new MealCard("Instance 3", 3),
                new MealCard("Instance 4", 4)
            }),
            "Construis une grille generique.");
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "EvaluateStructuredColumnCoverage",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var evidenceIds = rawBundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();

        object Evaluate(params string[][] compatibleColumns)
        {
            var annotatedBundle = rawBundle with
            {
                Items = rawBundle.Items
                    .Select((item, index) => item with
                    {
                        SelectionHints = new Dictionary<string, string>(
                            item.SelectionHints,
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["semanticDisplayValue"] = "Instance " + (index + 1),
                            ["semanticCompatibleColumnLabels"] =
                                JsonSerializer.Serialize(compatibleColumns[index])
                        }
                    })
                    .ToArray()
            };
            return method!.Invoke(
                null,
                new object?[]
                {
                    annotatedBundle,
                    evidenceIds,
                    evidenceIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new[] { "Ligne 1", "Ligne 2" },
                    new[] { "Option A", "Option B" }
                })!;
        }

        var insufficient = Evaluate(
            new[] { "Option A" },
            new[] { "Option A" },
            new[] { "Option A" },
            new[] { "Option A" });
        Assert.True(ReadPrivateProperty<bool>(insufficient, "Applied"));
        Assert.False(ReadPrivateProperty<bool>(
            insufficient,
            "HasRequiredCoverage"));
        Assert.Equal(2, ReadPrivateProperty<int>(
            insufficient,
            "MatchedCellCount"));

        var complete = Evaluate(
            new[] { "Option A" },
            new[] { "Option A" },
            new[] { "Option B" },
            new[] { "Option B" });
        Assert.True(ReadPrivateProperty<bool>(complete, "Applied"));
        Assert.True(ReadPrivateProperty<bool>(
            complete,
            "HasRequiredCoverage"));
        Assert.Equal(4, ReadPrivateProperty<int>(complete, "MatchedCellCount"));
    }

    [Fact]
    public void ResearchTransition_ReceivesCanonicalRolesAndLlmAuthoredPoolMemory()
    {
        var rawBundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance A1", 1),
                new MealCard("Instance A2", 2),
                new MealCard("Instance B1", 3),
                new MealCard("Instance sans role", 4)
            }),
            "Construis une grille generique.");
        var compatibility = new[]
        {
            new[] { "Option A" },
            new[] { "Option A" },
            new[] { "Option B" },
            Array.Empty<string>()
        };
        var annotatedBundle = rawBundle with
        {
            Items = rawBundle.Items
                .Select((item, index) => item with
                {
                    SelectionHints = new Dictionary<string, string>(
                        item.SelectionHints,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["semanticDisplayValue"] = GetTestDisplayValue(index),
                        ["semanticCompatibleColumnLabels"] =
                            JsonSerializer.Serialize(compatibility[index])
                    }
                })
                .ToArray()
        };
        var intake = Intake("Construis une grille generique.") with
        {
            CanonicalColumnSemanticRoles =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Option A"] = "Premiere famille semantique.",
                    ["Option B"] = "Deuxieme famille semantique."
                }
        };
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildLlmAuthoredRoleInventoryContext",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var context = Assert.IsType<string>(method!.Invoke(
            null,
            new object?[] { intake, annotatedBundle, 12 }));

        Assert.Contains(
            "ROLES SEMANTIQUES CANONIQUES DEJA DECIDES PAR LE LLM",
            context);
        Assert.Contains("Option A => Premiere famille semantique.", context);
        Assert.Contains("Option B => Deuxieme famille semantique.", context);
        Assert.Contains("Option A | capacite=2", context);
        Assert.Contains("E1 | Instance A1", context);
        Assert.Contains("E2 | Instance A2", context);
        Assert.Contains("Option B | capacite=1", context);
        Assert.Contains("E3 | Instance B1", context);
        Assert.Contains("APPROUVES SANS ROLE COMPATIBLE | capacite=1", context);
        Assert.DoesNotContain("E4 | Instance sans role", context);

        static string GetTestDisplayValue(int index)
            => index switch
            {
                0 => "Instance A1",
                1 => "Instance A2",
                2 => "Instance B1",
                _ => "Instance sans role"
            };
    }

    [Fact]
    public async Task NavigationAnchorAudit_ExposesOnlyAnchorsAcceptedByTheLlm()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ResolvableNavigationResult(),
            "Trouve une option documentee.");
        var evidenceIds = bundle.Items
            .Where(static item => item.SourceKind == "navigation_map")
            .Select(static item => item.EvidenceId)
            .ToArray();
        Assert.Equal(2, evidenceIds.Length);
        var llm = new ScriptedAgentLlm(Completion(CandidateAuditCall(
            "navigation-anchor-audit",
            new[] { evidenceIds[1] },
            new[] { evidenceIds[0] })));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with
            {
                MaximumSemanticCandidatesPerAuditBatch = 6,
                SemanticCandidateAuditEnabled = true
            });
        var auditedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "CompleteNavigationAnchorEligibilityAuditAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            new object?[]
            {
                Intake("Trouve une option documentee."),
                "LIVRABLE: une option documentee",
                "option documentee",
                "Le libelle doit nommer une instance autonome du type cible.",
                bundle,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                auditedIds,
                CancellationToken.None
            }));

        await task;
        var execution = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var annotatedBundle = ReadPrivateProperty<EvidenceBundle>(
            execution,
            "Bundle");
        Assert.True(ReadPrivateProperty<bool>(execution, "Attempted"));
        Assert.True(ReadPrivateProperty<bool>(execution, "ProtocolValid"));
        Assert.Equal(1, ReadPrivateProperty<int>(execution, "EligibleCount"));
        Assert.Equal(2, auditedIds.Count);
        Assert.Equal(
            "false",
            annotatedBundle.ById[evidenceIds[0]].SelectionHints[
                "semanticNavigationAnchorEligible"]);
        Assert.Equal(
            "true",
            annotatedBundle.ById[evidenceIds[1]].SelectionHints[
                "semanticNavigationAnchorEligible"]);

        var toolsMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildResearchTransitionTools",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(toolsMethod);
        var tools = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentToolDefinition>>(toolsMethod!.Invoke(
            null,
            new object?[]
            {
                annotatedBundle,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<RetrievalRequest>(),
                12
            }));
        var resolve = Assert.Single(
            tools,
            static tool => tool.Name == "resolve_navigation_anchors");
        Assert.Equal(
            new[] { evidenceIds[1] },
            resolve.Parameters
                .GetProperty("properties")
                .GetProperty("evidenceIds")
                .GetProperty("items")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
    }

    [Fact]
    public void StructuredCandidateWriter_ReceivesLlmAuthoredColumnCompatibility()
    {
        var rawBundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance A", 1),
                new MealCard("Instance sans role", 2)
            }),
            "Construis une grille generique.");
        var annotatedBundle = rawBundle with
        {
            Items = rawBundle.Items
                .Select((item, index) => item with
                {
                    SelectionHints = new Dictionary<string, string>(
                        item.SelectionHints,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["semanticDisplayValue"] = index == 0
                            ? "Instance A"
                            : "Instance sans role",
                        ["semanticCompatibleColumnLabels"] =
                            JsonSerializer.Serialize(index == 0
                                ? new[] { "Option A" }
                                : Array.Empty<string>())
                    }
                })
                .ToArray()
        };
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildStructuredCandidateWriterMessages",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var messages = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentMessage>>(method!.Invoke(
            null,
            new object?[]
            {
                Intake("Construis une grille generique."),
                annotatedBundle,
                annotatedBundle.Items.Select(static item => item.EvidenceId).ToArray(),
                "Periode",
                new[] { "Premiere" },
                new[] { "Option A", "Option B" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Option A"] = "Premiere famille.",
                    ["Option B"] = "Deuxieme famille."
                },
                null
            })!);
        var prompt = string.Join(
            Environment.NewLine,
            messages.Select(static message => message.Content));
        Assert.Contains(
            "colonnes compatibles decidees par le LLM: Option A",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "colonnes compatibles decidees par le LLM: AUCUNE",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredWorkspaceHandoff_RendersTheOrderedLlmSelectionWithoutASecondSelectionCall()
    {
        var selectedIds = new[] { "E1", "E2", "E3", "E4" };
        var initialAction = Call(
            "router-cards",
            "documents_content_cards",
            new
            {
                categoryPath = "Knowledge",
                limit = 4,
                offset = 0
            });
        var intake = Intake("Construis la grille sourcee demandee.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialAction.Id,
                    initialAction.Name,
                    initialAction.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "grille sourcee",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "options documentees",
                    initialCapability = "documents_content_cards",
                    rowHeader = "Periode",
                    rowLabels = new[] { "Premiere", "Deuxieme" },
                    columns = new[] { "Option A", "Option B" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            CandidateStrategy(candidateObjectType: "options documentees"),
            Completion(Call("workspace-grid-ready", "manage_evidence_workspace", new
            {
                status = "ready_to_write",
                retainEvidenceIds = selectedIds,
                note = "Ordre final des quatre cellules du layout."
            })),
            SemanticReview(
                "accept",
                "Les quatre options ordonnees sont distinctes et sourcees."),
            StructuredBatchReview("C01", "C02"),
            StructuredBatchReview("C03", "C04"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Option documentee A", 1),
                new MealCard("Option documentee B", 2),
                new MealCard("Option documentee C", 3),
                new MealCard("Option documentee D", 4)
            })),
            Options() with
            {
                MaximumTurns = 4,
                MaximumObservationItems = 8,
                MaximumWorkingEvidenceItems = 8,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            selectedIds,
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        Assert.Equal(5, llm.Requests.Count);
        var workspaceTool = Assert.Single(
            llm.ToolSets[1],
            static tool => tool.Name == "manage_evidence_workspace");
        Assert.Contains(
            "ready_to_write",
            workspaceTool.Parameters.GetRawText(),
            StringComparison.Ordinal);
        Assert.Contains(
            "ordre des cellules du layout canonique",
            workspaceTool.Parameters.GetRawText(),
            StringComparison.Ordinal);
        var workspaceProperties = workspaceTool.Parameters
            .GetProperty("properties");
        Assert.False(workspaceProperties.TryGetProperty(
            "rejectEvidenceIds",
            out _));
        var workspaceStatuses = workspaceProperties
            .GetProperty("status")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .ToArray();
        Assert.Equal(new[] { "ready_to_write" }, workspaceStatuses);
        var retainedIdsSchema = workspaceProperties
            .GetProperty("retainEvidenceIds");
        Assert.Equal(4, retainedIdsSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(4, retainedIdsSchema.GetProperty("maxItems").GetInt32());
        Assert.Equal(
            selectedIds,
            retainedIdsSchema
                .GetProperty("items")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static value => value.GetString()));
        Assert.DoesNotContain(
            llm.ToolSets.SelectMany(static tools => tools),
            static tool => tool.Name == "submit_evidence_selection");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.evidence_workspace.ready_to_write_accepted"
            && trace.Fields["selected_evidence_ids"] == "E1,E2,E3,E4"
            && trace.Fields["rows"] == "2"
            && trace.Fields["columns"] == "2");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.structured_renderer.completed"
            && trace.Fields["selection_source"]
                == "llm_evidence_workspace");
    }

    [Fact]
    public async Task SplitActionWriter_WorkspaceHandoffRejectsDuplicateVisibleSources()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: deux options sourcees\nDIMENSIONS: deux options\n"
                + "PREUVES_ATOMIQUES: 2 options nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: options documentees\n"
                + "APPROCHE_OUTILS: inventaire puis decision\n"
                + "ACCEPTER_SI: deux options distinctes\n"
                + "INSUFFISANT_SEULEMENT_SI: moins de deux options"),
            Completion(Call("context", "documents_context", new
            {
                docPath = "Knowledge/options.pdf",
                pageStart = 1,
                pageEnd = 2
            })),
            Completion(Call("workspace-duplicate", "manage_evidence_workspace", new
            {
                status = "ready_to_write",
                retainEvidenceIds = new[] { "E1", "E2" },
                note = "Premiere selection."
            })),
            Completion(Call("workspace-ready", "manage_evidence_workspace", new
            {
                status = "ready_to_write",
                retainEvidenceIds = new[] { "E1", "E3" },
                note = "Une preuve par source visible."
            })),
            Completion("Option documentee A [E1]\nOption documentee C [E3]"),
            SemanticReview(
                "accept",
                "Les deux options proviennent de sources visibles distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContextResultWithVisibleSourceDuplicate(1, 3)),
            Options() with
            {
                MaximumTurns = 5,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne deux options documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "E1", "E3" },
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        var readyWorkspaceTool = Assert.Single(
            llm.ToolSets[2],
            static tool => tool.Name == "manage_evidence_workspace");
        var allowedReadyIds = readyWorkspaceTool.Parameters
            .GetProperty("properties")
            .GetProperty("retainEvidenceIds")
            .GetProperty("items")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .ToArray();
        Assert.Equal(new[] { "E1", "E3" }, allowedReadyIds);
        Assert.DoesNotContain("E2", allowedReadyIds);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.evidence_workspace.rejected"
            && trace.Fields["error"].Contains(
                "workspace_ready_to_write_requires_unique_visible_sources",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task StructuredInventory_LetsLlmAuditEveryCandidateBeforeFinalSelection()
    {
        var cards = TwentyMealCards()
            .Concat(new[]
            {
                new MealCard("INGREDIENTS PREPARATION", 21),
                new MealCard("Servir cette recette", 22),
                new MealCard("Astuce Realiser la recette", 23),
                new MealCard("Pliez afin de terminer", 24)
            })
            .ToArray();
        var approvedIds = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var rejectedIds = Enumerable.Range(21, 4)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 24
            })),
            Completion(CandidateAuditCall(
                "candidate-audit",
                approvedIds,
                rejectedIds)),
            Completion(SelectionCall("selection", approvedIds)),
            SemanticReview(
                "accept",
                "Les vingt recettes retenues sont completes, distinctes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                MaximumSemanticCandidatesPerAuditBatch = 40,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var auditRequestIndex = Enumerable.Range(0, llm.Requests.Count)
            .Where(index => RequestText(llm, index).Contains(
                "CANDIDATS A JUGER DANS CE LOT:",
                StringComparison.Ordinal))
            .Single();
        Assert.Empty(llm.ToolSets[auditRequestIndex]);
        Assert.Contains(llm.StructuredOutputContracts, static contract =>
            contract.Name == "source_backed_candidate_batch_audit_v5");
        Assert.Contains("c24 [E24]", RequestText(llm, auditRequestIndex));
        var selectionIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool => tool.Name == "submit_evidence_selection"));
        Assert.True(selectionIndex >= 0);
        Assert.DoesNotContain("E21 |", RequestText(llm, selectionIndex));
        Assert.DoesNotContain(
            "\"evidenceId\":\"E21\"",
            RequestText(llm, selectionIndex));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["candidates"] == "24"
            && trace.Fields["approved"] == "20"
            && trace.Fields["rejected"] == "4"
            && trace.Fields["rejected_evidence_ids"] == "E21,E22,E23,E24");
    }

    [Fact]
    public async Task StructuredCandidateAudit_TellsTheOrchestratorWhenNoNewCandidateWasApproved()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: grille sourcee\nDIMENSIONS: 2 x 1\n"
                + "PREUVES_ATOMIQUES: 2 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 2 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 2 instances"),
            Completion(Call("cards-no-yield", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 1,
                offset = 0
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-no-yield",
                Array.Empty<string>(),
                new[] { "E1" })),
            Completion(Call("cards-pivot", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 2,
                offset = 1
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-pivot",
                new[] { "E2", "E3" },
                Array.Empty<string>())),
            Completion(Call(
                "selection",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            SemanticReview(
                "accept",
                "Les deux valeurs retenues sont completes et distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(new[]
                {
                    new MealCard("INGREDIENTS PREPARATION", 1)
                }),
                ContentCardInventoryResult(new[]
                {
                    new MealCard("Option documentee A", 2),
                    new MealCard("Option documentee B", 3)
                })),
            Options() with
            {
                MaximumTurns = 6,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de deux options documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            "0 sources distinctes approuvees sur 2",
            RequestText(llm, 3),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "poursuis librement la collecte",
            RequestText(llm, 3),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StructuredCollection_ReportsAZeroYieldActionAsNoProgressToTheLlm()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: grille sourcee\nDIMENSIONS: 2 x 1\n"
                + "PREUVES_ATOMIQUES: 2 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 2 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 2 instances"),
            Completion(Call("cards-first", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 1,
                offset = 0
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-first",
                new[] { "E1" },
                Array.Empty<string>())),
            Completion(Call("cards-zero-yield", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                q = "already observed option",
                limit = 1,
                offset = 0
            })),
            Completion(Call("cards-new-route", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                q = "another documented option",
                limit = 1,
                offset = 0
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-second",
                new[] { "E2" },
                Array.Empty<string>())),
            Completion(Call(
                "selection",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1", "E2" },
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            SemanticReview(
                "accept",
                "Les deux valeurs retenues sont completes et distinctes."));
        var firstCard = new[] { new MealCard("Option documentee A", 1) };
        var secondCard = new[] { new MealCard("Option documentee B", 2) };
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(firstCard),
                ContentCardInventoryResult(firstCard),
                ContentCardInventoryResult(secondCard)),
            Options() with
            {
                MaximumTurns = 7,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de deux options documentees."),
            CancellationToken.None);

        var allLlmRequests = string.Join(
            "\n--- REQUEST ---\n",
            Enumerable.Range(0, llm.Requests.Count)
                .Select(index => RequestText(llm, index)));
        Assert.NotNull(result.RetrievalPlan);
        Assert.Equal(
            new int?[] { 1, 0, 1 },
            result.RetrievalPlan!.Requests
                .Select(static request => request.NewEvidenceCount)
                .ToArray());
        Assert.Contains(
            "nouvelles_preuves=0",
            allLlmRequests,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "rendement=aucune_nouvelle_preuve",
            allLlmRequests,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Une continuation conserve exactement la route",
            allLlmRequests,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.documentary_progress.none"
            && trace.Fields["duplicate_calls"] == "0"
            && trace.Fields["zero_new_evidence_calls"] == "1"
            && trace.Fields["consecutive_no_progress_turns"] == "1");
    }

    [Fact]
    public async Task StructuredCandidateAudit_ClassifiesEveryCandidateAndRetainsOnlyNamedSourceItems()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: reponse sourcee\nDIMENSIONS: 2 x 1\n"
                + "PREUVES_ATOMIQUES: 2 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 2 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 2 instances"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 6
            })),
            Completion(CandidateAuditCall(
                "candidate-audit",
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["E1"] = 0,
                    ["E2"] = 0,
                    ["E3"] = 1,
                    ["E4"] = 0,
                    ["E5"] = 0,
                    ["E6"] = 1
                })),
            Completion(Call(
                "selection",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E3", "E6" },
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            SemanticReview(
                "accept",
                "L'instance nommee est directement soutenue."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("DEJEUNER", 1),
                new MealCard("Petit-dejeuner (4h-5h)", 2),
                new MealCard("Boeuf bourguignon", 3),
                new MealCard("Planification des repas", 4),
                new MealCard("Quantite de farine", 5),
                new MealCard("Tarte aux pommes", 6)
            })),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "E3", "E6" },
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        Assert.Contains("Boeuf bourguignon [E3]", result.Answer);
        Assert.Contains("Tarte aux pommes [E6]", result.Answer);
        Assert.DoesNotContain("DEJEUNER [E1]", result.Answer);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["approved_evidence_ids"] == "E3,E6"
            && trace.Fields["rejected_evidence_ids"] == "E1,E2,E4,E5"
            && trace.Fields["audit_semantic_decision_counts"].Contains(
                "named_source_item=2",
                StringComparison.Ordinal)
            && trace.Fields["audit_semantic_decision_counts"].Contains(
                "other_wrong_type=4",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task StructuredCandidateAudit_SuppressesAnEvidenceItemWithoutRenderableTextBeforeLlmJudgment()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: grille sourcee\nDIMENSIONS: 2 x 1\n"
                + "PREUVES_ATOMIQUES: 2 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 2 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 2 instances"),
            Completion(Call("search", "rag_search", new
            {
                query = "documented options"
            })),
            Completion(CandidateAuditCall(
                "candidate-audit",
                new[] { "E1", "E3" },
                Array.Empty<string>())),
            Completion(Call(
                "selection",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1", "E3" },
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            SemanticReview(
                "accept",
                "Les deux valeurs retenues sont completes et distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultWithAnEmptyEvidenceItem()),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de deux options documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var auditPrompt = Enumerable.Range(0, llm.Requests.Count)
            .Select(index => RequestText(llm, index))
            .Single(static text => text.Contains(
                "CANDIDATS A JUGER DANS CE LOT:",
                StringComparison.Ordinal))
            ;
        Assert.Contains("[E1]", auditPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[E2]", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("[E3]", auditPrompt, StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
            == "source_backed_agent_v2.evidence.non_renderable_suppressed"
            && trace.Fields["evidence_ids"] == "E2"
            && trace.Fields["decision_source"] == "mechanical_contract");
    }

    [Fact]
    public async Task StructuredCandidateAudit_PreservesTheBackendCanonicalTitleWithoutRelabeling()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: grille sourcee\nDIMENSIONS: 2 x 1\n"
                + "PREUVES_ATOMIQUES: 2 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 2 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 2 instances"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 2
            })),
            Completion(CandidateAuditCall(
                "candidate-audit",
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["E1"] = 1,
                    ["E2"] = 1
                })),
            Completion(Call(
                "selection",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1", "E2" },
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            SemanticReview(
                "accept",
                "Les deux ancres canoniques nomment des objets distincts."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard(
                    "Tartiflette",
                    1,
                    "Plats > Tartiflette"),
                new MealCard(
                    "Gratin dauphinois",
                    2,
                    "Plats > Gratin dauphinois")
            })),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 8,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis la grille sourcee demandee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("Tartiflette [E1]", result.Answer);
        Assert.Contains("Gratin dauphinois [E2]", result.Answer);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["approved_display_values"]
                .Contains("E1=Tartiflette", StringComparison.Ordinal)
            && trace.Fields["approved_display_values"]
                .Contains("E2=Gratin dauphinois", StringComparison.Ordinal));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["label_review_llm_calls"] == "0");
    }

    [Fact]
    public async Task StructuredSearch_AuditsMaterializedCanonicalCardsBeforeFinalSelection()
    {
        var approvedIds = Enumerable.Range(2, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call(
                "column-semantics",
                "submit_column_semantics",
                new
                {
                    columns = new[]
                    {
                        new
                        {
                            label = "Petit-dejeuner",
                            inclusion = "Premiere prise principale qui ouvre la journee.",
                            exclusion = "Repas principaux tardifs et prises intermediaires."
                        },
                        new
                        {
                            label = "Dejeuner",
                            inclusion = "Repas principal pris au milieu de la journee.",
                            exclusion = "Prises du matin, collations et repas du soir."
                        },
                        new
                        {
                            label = "Collation",
                            inclusion = "Prise intermediaire plus legere entre les repas principaux.",
                            exclusion = "Repas principaux complets et prises matinales."
                        },
                        new
                        {
                            label = "Souper",
                            inclusion = "Repas principal qui clot la journee.",
                            exclusion = "Prises matinales, dejeuners et collations legeres."
                        }
                    }
                })),
            Completion(Call("search", "rag_search", new
            {
                query = "recettes documentees",
                categoryPath = "Cuisine"
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-search",
                approvedIds,
                new[] { "E22" })),
            Completion(SelectionCall("selection", approvedIds)),
            SemanticReview(
                "accept",
                "Les vingt recettes retenues sont completes, distinctes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultWithCards(
                "Cuisine/recettes.pdf",
                "Cuisine",
                "recette",
                page: 12,
                cardCount: 21)),
            Options() with
            {
                MaximumTurns = 4,
                MaximumObservationItems = 24,
                MaximumWorkingEvidenceItems = 40,
                MaximumSemanticCandidatesPerAuditBatch = 40,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                SemanticColumnRoleReviewEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            Enumerable.Range(0, llm.Requests.Count),
            index => RequestText(llm, index).Contains(
                    "[E22]",
                StringComparison.Ordinal));
        Assert.Equal(
            "submit_column_semantics",
            Assert.Single(llm.ToolSets[1]).Name);
        Assert.True(llm.RequireToolCalls[1]);
        Assert.Equal(0, llm.Temperatures[1]);
        Assert.Equal(320, llm.MaxTokens[1]);
        Assert.Contains("exactement 4 colonnes", RequestText(llm, 1));
        var selectionRequestIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool => tool.Name == "submit_evidence_selection"));
        Assert.True(selectionRequestIndex >= 0);
        Assert.DoesNotContain("E22 |", RequestText(llm, selectionRequestIndex));
        Assert.Contains(
            "ROLES SEMANTIQUES DES COLONNES",
            RequestText(llm, selectionRequestIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Prise intermediaire plus legere",
            RequestText(llm, selectionRequestIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(llm.Requests.Count >= 6);
        var selectionTool = Assert.Single(llm.ToolSets[selectionRequestIndex]);
        var selectionColumnEnum = selectionTool.Parameters
            .GetProperty("properties")
            .GetProperty("layout")
            .GetProperty("properties")
            .GetProperty("columns")
            .GetProperty("items")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        Assert.Equal(
            new[] { "Petit-dejeuner", "Dejeuner", "Collation", "Souper" },
            selectionColumnEnum);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_column_roles.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["roles"] == "4");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["candidates"] == "21"
            && trace.Fields["approved"] == "20"
            && trace.Fields["rejected"] == "1"
            && trace.Fields["rejected_evidence_ids"] == "E22");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
    }

    [Fact]
    public async Task StructuredSelection_KeepsRetrievalToolsAfterBaseTurnBudgetWhenApprovedPoolIsShort()
    {
        var firstApprovedIds = Enumerable.Range(1, 17)
            .Select(static index => $"E{index}")
            .ToArray();
        var additionalApprovedIds = new[] { "E22", "E23", "E24" };
        var finalSelection = firstApprovedIds
            .Concat(additionalApprovedIds)
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "PREMIERE_ACTION: documents_content_cards "
                + "{\"categoryPath\":\"Cuisine\",\"limit\":20,\"offset\":0}\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(CandidateAuditCall(
                "candidate-audit-inventory",
                firstApprovedIds,
                new[] { "E18", "E19", "E20" })),
            Completion(Call("search-more", "start_document_search", new
            {
                query = "autres recettes documentees",
                limit = 3
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-search",
                additionalApprovedIds,
                Array.Empty<string>())),
            Completion(SelectionCall("selection", finalSelection)),
            SemanticReview(
                "accept",
                "Les vingt recettes retenues sont completes, distinctes et sourcees."));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(TwentyMealCards()),
            SearchResultWithCards(
                "Cuisine/autres-recettes.pdf",
                "Cuisine",
                "autre-recette",
                page: 30,
                cardCount: 3));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 5,
                MaximumSemanticCorrectionTurns = 2,
                MaximumObservationItems = 8,
                MaximumWorkingEvidenceItems = 40,
                MaximumSemanticCandidatesPerAuditBatch = 20,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            llm.ToolSets,
            static toolSet => toolSet.Any(
                static tool => tool.Name == "start_document_search"));
        var transitionRequestIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool => tool.Name == "start_document_search"));
        Assert.True(transitionRequestIndex >= 0);
        Assert.Contains(
            "audit_llm_approuvees=17",
            RequestText(llm, transitionRequestIndex),
            StringComparison.Ordinal);
        Assert.Contains(
            "audit_llm_refusees=3",
            RequestText(llm, transitionRequestIndex),
            StringComparison.Ordinal);
        var e22AuditPrompt = Enumerable.Range(0, llm.Requests.Count)
            .Select(index => RequestText(llm, index))
            .Single(static text =>
                text.Contains(
                    "CANDIDATS A JUGER DANS CE LOT:",
                    StringComparison.Ordinal)
                && text.Contains("[E22]", StringComparison.Ordinal));
        Assert.DoesNotContain(
                    "[E1]",
            e22AuditPrompt,
            StringComparison.Ordinal);
        Assert.Equal(
            new[] { "documents.content_cards", "rag.search" },
            executor.ToolNames);
    }

    [Fact]
    public async Task StructuredInventory_RejectsInvalidIndependentCandidateProtocol()
    {
        var cards = TwentyMealCards()
            .Concat(new[]
            {
                new MealCard("Recette documentee 21", 21),
                new MealCard("Recette documentee 22", 22),
                new MealCard("Recette documentee 23", 23),
                new MealCard("Recette documentee 24", 24)
            })
            .ToArray();
        var partiallyApprovedIds = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 24
            })),
            Completion("invalid independent decision"),
            Completion("still invalid independent decision"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumTurns = 3,
                MaximumSemanticCorrectionTurns = 0,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["protocol_valid"] == "false"
            && trace.Fields["approved"] == "0"
            && trace.Fields["rejected"] == "0"
            && trace.Fields["llm_calls"] == "2"
            && trace.Fields["withheld"] == "24"
            && trace.Fields["failure_reason"]
                == "candidate_batch_audit_structured_object_required");
        Assert.DoesNotContain(
            llm.ToolSets.SelectMany(static toolSet => toolSet),
            static tool => tool.Name == "submit_evidence_selection");
    }

    [Fact]
    public async Task StructuredInventory_RecompactsCollectionDecisionAfterContextOverflow()
    {
        var cards = TwentyMealCards()
            .Concat(new[]
            {
                new MealCard("Recette documentee 21", 21),
                new MealCard("Recette documentee 22", 22),
                new MealCard("Recette documentee 23", 23),
                new MealCard("Recette documentee 24", 24)
            })
            .ToArray();
        var approvedIds = Enumerable.Range(1, 24)
            .Select(static index => $"E{index}")
            .ToArray();
        var selectedIds = approvedIds.Take(20).ToArray();
        var llm = new ContextOverflowOnceLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 24
            })),
            new HttpRequestException(
                "LLM request failed: 400 Bad Request. Body: request (4300 tokens) exceeds the available context size (4096 tokens)",
                null,
                System.Net.HttpStatusCode.BadRequest),
            Completion(CandidateAuditCall(
                "candidate-audit-compact-global",
                approvedIds,
                Array.Empty<string>())),
            Completion(SelectionCall("selection", selectedIds)),
            SemanticReview(
                "accept",
                "Les vingt recettes selectionnees sont completes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                MaximumSemanticCandidatesPerAuditBatch = 40,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                MaximumContextTokens = 4096
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var e1AuditPrompts = Enumerable.Range(0, llm.Requests.Count)
            .Select(index => llm.Requests[index]
                .Select(static message => message.Content ?? string.Empty)
                .LastOrDefault(static text => text.Contains(
                    "CANDIDATS A JUGER DANS CE LOT:",
                    StringComparison.Ordinal))
                ?? string.Empty)
            .Where(static text => text.Contains(
                "[E1]",
                StringComparison.Ordinal))
            .ToArray();
        Assert.InRange(e1AuditPrompts.Length, 2, 4);
        var retriedE1Prompt = e1AuditPrompts[^1];
        Assert.DoesNotContain(
            " | fichier=",
            retriedE1Prompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["mode"] == "batched_semantic_decision"
            && trace.Fields["llm_calls"] == "1");
    }

    [Fact]
    public async Task StructuredInventory_AuditsAPaginationPageWithoutPadding()
    {
        var approvedIds = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards-page", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20,
                offset = 40
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-page",
                approvedIds,
                Array.Empty<string>())),
            Completion(SelectionCall("selection", approvedIds)),
            SemanticReview(
                "accept",
                "Les vingt recettes de la page sont completes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(TwentyMealCards())),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                MaximumSemanticCandidatesPerAuditBatch = 20,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            1,
            Enumerable.Range(0, llm.Requests.Count).Count(index =>
                RequestText(llm, index).Contains(
                    "CANDIDATS A JUGER DANS CE LOT:",
                    StringComparison.Ordinal)));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["candidates"] == "20"
            && trace.Fields["approved"] == "20");
    }

    [Fact]
    public async Task StructuredInventory_AuditsTheGlobalCollectionThroughIndependentDecisions()
    {
        var cards = TwentyMealCards()
            .Concat(Enumerable.Range(21, 20)
                .Select(static index => new MealCard(
                    "Libelle candidat " + index,
                    index)))
            .ToArray();
        var finalApprovedIds = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var rejectedIds = Enumerable.Range(21, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 40
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-global",
                finalApprovedIds,
                rejectedIds)),
            Completion(SelectionCall("selection", finalApprovedIds)),
            SemanticReview(
                "accept",
                "La reconciliation globale a conserve vingt recettes distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                MaximumSemanticCandidatesPerAuditTurn = 40,
                MaximumSemanticCandidatesPerAuditBatch = 40,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var e40Prompt = Enumerable.Range(0, llm.Requests.Count)
            .Select(index => RequestText(llm, index))
            .Single(static text => text.Contains(
                "[E40]",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            "reconciliateur semantique global",
            e40Prompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["mode"] == "batched_semantic_decision"
            && trace.Fields["llm_calls"] == "1"
            && trace.Fields["candidates"] == "40"
            && trace.Fields["approved"] == "20"
            && trace.Fields["rejected"] == "20");
    }

    [Fact]
    public async Task StructuredInventory_PagesCandidateAuditWithoutLosingTheRemainder()
    {
        var cards = Enumerable.Range(1, 80)
            .Select(static index => new MealCard(
                "Recette documentee " + index,
                index))
            .ToArray();
        var firstApprovedIds = Enumerable.Range(1, 12)
            .Select(static index => $"E{index}")
            .ToArray();
        var firstRejectedIds = Enumerable.Range(13, 60)
            .Select(static index => $"E{index}")
            .ToArray();
        var secondApprovedIds = Enumerable.Range(73, 8)
            .Select(static index => $"E{index}")
            .ToArray();
        var finalSelection = firstApprovedIds
            .Concat(secondApprovedIds)
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 80
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-page-1",
                firstApprovedIds,
                firstRejectedIds)),
            Completion(CandidateAuditCall(
                "candidate-audit-page-2",
                secondApprovedIds,
                Array.Empty<string>())),
            Completion(SelectionCall("selection", finalSelection)),
            SemanticReview(
                "accept",
                "Les vingt recettes retenues sont completes, distinctes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumTurns = 4,
                MaximumContextTokens = 8192,
                MaximumWorkingEvidenceItems = 80,
                MaximumSemanticCandidatesPerAuditTurn = 72,
                MaximumSemanticCandidatesPerAuditBatch = 72,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var auditPrompts = Enumerable.Range(0, llm.Requests.Count)
            .Select(index => RequestText(llm, index))
            .Where(static text => text.Contains(
                "CANDIDATS A JUGER DANS CE LOT:",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(auditPrompts, static text => text.Contains(
            "[E72]",
            StringComparison.Ordinal));
        Assert.Contains(auditPrompts, static text => text.Contains(
            "[E73]",
            StringComparison.Ordinal));
        Assert.Contains(auditPrompts, static text => text.Contains(
            "[E80]",
            StringComparison.Ordinal));
        var auditTraces = result.TraceEvents
            .Where(static trace => trace.EventName
                == "source_backed_agent_v2.candidate_audit.completed")
            .ToArray();
        Assert.Collection(
            auditTraces,
            first =>
            {
                Assert.Equal("72", first.Fields["candidates"]);
                Assert.Equal("80", first.Fields["pending_before_audit"]);
                Assert.Equal("12", first.Fields["approved_sources"]);
            },
            second =>
            {
                Assert.Equal("8", second.Fields["candidates"]);
                Assert.Equal("8", second.Fields["pending_before_audit"]);
                Assert.Equal("20", second.Fields["approved_sources"]);
            });
    }

    [Fact]
    public async Task CandidateAudit_UsesOneBatchedDecisionForAllCandidates()
    {
        var cards = Enumerable.Range(1, 48)
            .Select(static index => new MealCard(
                "Documented option " + index,
                index))
            .ToArray();
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(cards),
            "Give me documented options.");
        var approvedIds = bundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();
        var llm = new ScriptedAgentLlm(Completion(CandidateAuditCall(
            "candidate-audit-batch",
            approvedIds,
            Array.Empty<string>())));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with
            {
                MaximumSemanticCandidatesPerAuditTurn = 48,
                MaximumSemanticCandidatesPerAuditBatch = 48
            });
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "CompleteBatchedCandidateAuditAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            runner,
            new object?[]
            {
                Intake("Give me documented options."),
                "LIVRABLE: documented options",
                "named documented option",
                "Le libelle exact nomme une instance autonome du type cible.",
                "",
                Array.Empty<string>(),
                Array.Empty<string>(),
                bundle.Items,
                CancellationToken.None
            }));

        await task;
        var execution = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.Equal(1, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(
            0,
            ReadPrivateProperty<int>(execution, "LabelReviewLlmCallCount"));
        Assert.Equal(48, ReadPrivateProperty<int>(
            execution,
            "CandidateDecisionCount"));
        Assert.Empty(Assert.Single(llm.ToolSets));
        var auditContract = Assert.Single(llm.StructuredOutputContracts);
        Assert.Equal(
            "source_backed_candidate_batch_audit_v5",
            auditContract.Name);
        var decisionsSchema = auditContract.Schema
            .GetProperty("properties")
            .GetProperty("decisions");
        Assert.Equal("array", decisionsSchema.GetProperty("type").GetString());
        Assert.Equal(48, decisionsSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(48, decisionsSchema.GetProperty("maxItems").GetInt32());
        var decisionItemSchema = decisionsSchema.GetProperty("items");
        Assert.Equal("integer", decisionItemSchema.GetProperty("type").GetString());
        Assert.Equal(0, decisionItemSchema.GetProperty("minimum").GetInt32());
        Assert.Equal(1, decisionItemSchema.GetProperty("maximum").GetInt32());
    }

    [Fact]
    public async Task CandidateAudit_ClassifiesApprovedCandidatesOneSemanticColumnAtATime()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance A1", 1),
                new MealCard("Instance A2", 2),
                new MealCard("Instance A3", 3),
                new MealCard("Instance A4", 4),
                new MealCard("Instance B1", 5),
                new MealCard("Instance B2", 6),
                new MealCard("Instance B3", 7),
                new MealCard("Instance B4", 8)
            }),
            "Construis une grille generique.");
        var approvedIds = bundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "candidate-audit-columns-1",
                approvedIds.Take(6).ToArray(),
                Array.Empty<string>())),
            Completion(CandidateAuditCall(
                "candidate-audit-columns-2",
                approvedIds.Skip(6).ToArray(),
                Array.Empty<string>())))
        {
            CandidateColumnCompatibilitySelector = (role, ids) =>
                string.Equals(role, "Option A", StringComparison.Ordinal)
                    ? ids.Where(static id =>
                        int.Parse(id.AsSpan(1),
                            System.Globalization.CultureInfo.InvariantCulture)
                        <= 4).ToArray()
                    : ids.Where(static id =>
                        int.Parse(id.AsSpan(1),
                            System.Globalization.CultureInfo.InvariantCulture)
                        > 4).ToArray()
        };
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "CompleteBatchedCandidateAuditAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var intake = Intake("Construis une grille generique.") with
        {
            CanonicalColumnSemanticRoles =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Option A"] = "Premiere famille semantique.",
                    ["Option B"] = "Deuxieme famille semantique."
                }
        };
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            new object?[]
            {
                intake,
                "LIVRABLE: grille generique",
                "instance documentee nommee",
                "Le libelle nomme une instance autonome.",
                "Periode",
                new[] { "Premiere", "Deuxieme" },
                new[] { "Option A", "Option B" },
                bundle.Items,
                CancellationToken.None
            }));

        await task;
        var execution = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.True(ReadPrivateProperty<bool>(
            execution,
            "ColumnCompatibilityApplied"));
        Assert.True(ReadPrivateProperty<bool>(
            execution,
            "ColumnCompatibilityProtocolValid"));
        Assert.Equal(4, ReadPrivateProperty<int>(
            execution,
            "ColumnCompatibilityLlmCallCount"));
        Assert.Equal(6, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(
            4,
            llm.StructuredOutputContracts.Count(static contract =>
                contract.Name
                == "source_backed_candidate_column_compatibility_v1"));

        var decision = execution.GetType()
            .GetProperty("Decision")!
            .GetValue(execution)!;
        var approvals = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
                decision.GetType().GetProperty("ApprovedCandidates")!.GetValue(decision))
            .Cast<object>()
            .ToArray();
        var compatibilityByEvidenceId = approvals.ToDictionary(
            approval => (string)approval.GetType()
                .GetProperty("EvidenceId")!
                .GetValue(approval)!,
            approval => ((IEnumerable<string>)approval.GetType()
                    .GetProperty("CompatibleColumnLabels")!
                    .GetValue(approval)!)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { "Option A" }, compatibilityByEvidenceId["E1"]);
        Assert.Equal(new[] { "Option A" }, compatibilityByEvidenceId["E2"]);
        Assert.Equal(new[] { "Option A" }, compatibilityByEvidenceId["E3"]);
        Assert.Equal(new[] { "Option A" }, compatibilityByEvidenceId["E4"]);
        Assert.Equal(new[] { "Option B" }, compatibilityByEvidenceId["E5"]);
        Assert.Equal(new[] { "Option B" }, compatibilityByEvidenceId["E6"]);
        Assert.Equal(new[] { "Option B" }, compatibilityByEvidenceId["E7"]);
        Assert.Equal(new[] { "Option B" }, compatibilityByEvidenceId["E8"]);
    }

    [Fact]
    public async Task CandidateAudit_MechanicallyRejectsAnApprovedValueThatIsExactlyALayoutAxis()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Breakfast", 1),
                new MealCard("Concrete recipe", 2)
            }),
            "Build a two-column plan.");
        var llm = new ScriptedAgentLlm(Completion(CandidateAuditCall(
            "candidate-audit-axis-identity",
            new[] { "E1", "E2" },
            Array.Empty<string>())));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "CompleteBatchedCandidateAuditAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            new object?[]
            {
                Intake("Build a two-column plan."),
                "LIVRABLE: a two-column plan",
                "named recipe",
                "The exact label names one concrete recipe.",
                "Day",
                new[] { "Monday" },
                new[] { "Breakfast", "Dinner" },
                bundle.Items,
                CancellationToken.None
            }));

        await task;
        var execution = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        Assert.DoesNotContain("E1=", completion.Content, StringComparison.Ordinal);
        Assert.Contains("E2=Concrete recipe", completion.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateAudit_RecognizesDocumentIdentityAsNonContentEvidence()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            DocumentIdentityOnlySearchResult(),
            "Résume ANSI B11.0-2023 en sept exigences utiles.");
        var item = Assert.Single(bundle.Items);

        Assert.True(
            SourceBackedAgentV2Runner
                .IsDocumentIdentityOnlyContentCandidateForTests(
                    "content_claim",
                    item,
                    "B11.0-2023"));
        Assert.False(
            SourceBackedAgentV2Runner
                .IsDocumentIdentityOnlyContentCandidateForTests(
                    "named_item",
                    item,
                    "B11.0-2023"));
    }

    [Fact]
    public async Task ContentClaimMode_UsesFlatAdequacyAndKeepsDistinctSamePageClaims()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "ANSI B11.0-2023 exigences",
            topK = 5
        });
        var intake = Intake("Donne deux exigences documentées de ANSI B11.0-2023.") with
        {
            QuestionFocus = "content",
            RequestedDocumentName =
                "ANSI B11.0-2023 - Safety of Machinery.pdf",
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "deux exigences documentées de ANSI B11.0-2023",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigence documentée",
                    atomicEvidenceMode = "content_claim",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-adequacy",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason =
                        "Les deux exigences distinctes couvrent exactement la demande."
                })),
            Completion(
                "Le protecteur doit empêcher l'accès à la zone dangereuse [E2]. "
                + "L'arrêt doit être accessible à l'opérateur [E3]."),
            SemanticReview(
                "accept",
                "Les deux exigences distinctes sont directement soutenues par E2 et E3."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "E2", "E3" },
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        var adequacyIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool =>
                tool.Name == "submit_flat_evidence_selection"));
        Assert.True(adequacyIndex >= 0);
        Assert.Contains("E1 | B11.0-2023", RequestText(llm, adequacyIndex));
        Assert.Contains(
            "E2 | The guard shall prevent access to the hazard zone",
            RequestText(llm, adequacyIndex));
        Assert.Contains(
            "E3 | The stop control shall be readily accessible to the operator",
            RequestText(llm, adequacyIndex));
        Assert.Contains(
            "fusion silencieuse",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "plusieurs documents",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "STRUCTURE DU LIVRABLE",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "n'a pas besoin d'exister deja",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "DOCUMENT EXPLICITEMENT NOMME",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ANSI B11.0-2023",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "submit_flat_evidence_context_gap",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            llm.ToolSets.SelectMany(static tools => tools),
            static tool => tool.Name == "submit_resolved_candidate_audit");
        Assert.DoesNotContain(
            result.TraceEvents,
            static trace => trace.EventName
                            == "source_backed_agent_v2.candidate_audit.completed");
        var semanticReviewIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool => tool.Name == "submit_semantic_review"));
        Assert.True(semanticReviewIndex >= 0);
        Assert.Contains(
            "MODE CONTENT_CLAIM",
            RequestText(llm, semanticReviewIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "une instruction",
            RequestText(llm, semanticReviewIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ou une action est une preuve valide",
            RequestText(llm, semanticReviewIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "fusion silencieuse",
            RequestText(llm, semanticReviewIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "verbe, relation, negation, ordre et portee",
            RequestText(llm, semanticReviewIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Une valeur doit etre une instance nommee",
            RequestText(llm, semanticReviewIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.action.semantic_selection_accepted"
            && trace.Fields["selected_evidence"] == "E2,E3");
        var writerIndex = adequacyIndex + 1;
        Assert.Empty(llm.ToolSets[writerIndex]);
        Assert.Contains(
            "[E2]",
            RequestText(llm, writerIndex),
            StringComparison.Ordinal);
        Assert.Contains(
            "[E3]",
            RequestText(llm, writerIndex),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentClaimMode_TreatsSelectedEvidenceAsAProofPoolForMultipleClaims()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "safety requirements",
            topK = 4
        });
        var intake = Intake("Donne deux exigences documentées de Safety Standard.") with
        {
            QuestionFocus = "content",
            RequestedDocumentName = "Safety Standard.pdf",
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "deux exigences documentées de Safety Standard",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigence documentée",
                    atomicEvidenceMode = "content_claim",
                    selectionPolicy = "explicit_set",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        const string answer =
            "Le protecteur doit empêcher l'accès à la zone dangereuse [E1]. "
            + "L'arrêt doit rester accessible à l'opérateur [E1].";
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-proof-pool",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1" },
                    reason =
                        "La fenêtre contient directement les deux exigences demandées."
                })),
            Completion(answer),
            SemanticReview(
                "accept",
                "Les deux claims sont distincts et directement soutenus par E1."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleEvidenceWithTwoClaimsSearchResult()),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(answer, result.Answer);
        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(
                result.Answer!,
                @"\[E1\]").Count);
        var writerIndex = llm.ToolSets.FindIndex(static tools => tools.Count == 0);
        Assert.True(writerIndex >= 0);
        Assert.Contains(
            "POOL DE PREUVES CONTENT_CLAIM",
            RequestText(llm, writerIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Un meme EvidenceId",
            RequestText(llm, writerIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "plusieurs claims distincts",
            RequestText(llm, writerIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Cite chaque EvidenceId exactement une fois",
            RequestText(llm, writerIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
    }

    [Fact]
    public void ContentClaimWriter_DoesNotPromoteAnUnselectedSamePageNeighbor()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            SamePageSourceWindowResult(),
            "Donne le fait documente selectionne.");
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildSelectedEvidenceWriterMessages",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var messages = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentMessage>>(method!.Invoke(
            null,
            new object?[]
            {
                Intake("Donne le fait documente selectionne."),
                bundle,
                 new[] { "E1" },
                 "content_claim",
                 null,
                 null,
                 null
             }));
        var prompt = string.Join(
                Environment.NewLine,
                messages.Select(static message => message.Content))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("[E1]\nTITRE:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[E2]\nTITRE:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlatAdequacyTournament_ShowsNewCandidatesAlongsidePriorLlmSelectionAndFeedback()
    {
        var cards = Enumerable.Range(1, 14)
            .Select(static index => new MealCard(
                $"Documented requirement {index}",
                index))
            .ToArray();
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(cards),
            "Summarize seven documented requirements.");
        var evidenceIds = bundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();
        var incumbentIds = evidenceIds.Take(7).ToArray();
        var reviewedIds = evidenceIds.Take(12).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var llm = new ScriptedAgentLlm(Completion(Call(
            "challenger-selection",
            "submit_flat_evidence_selection",
            new
            {
                evidenceIds = new[]
                {
                    evidenceIds[5], evidenceIds[6],
                    evidenceIds[12], evidenceIds[13]
                },
                reason = "The new requirements replace weaker prior candidates."
            })));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "ReviewFlatEvidenceAdequacyAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(SourceBackedIntake),
                typeof(string),
                typeof(EvidenceBundle),
                typeof(IReadOnlyList<string>),
                typeof(int),
                typeof(string),
                typeof(IReadOnlyList<string>),
                typeof(HashSet<string>),
                typeof(string),
                typeof(CancellationToken)
            },
            modifiers: null);
        Assert.NotNull(method);
        var reviewTask = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            new object?[]
            {
                Intake("Summarize seven documented requirements."),
                "Select the strongest documented requirements.",
                bundle,
                evidenceIds,
                7,
                "content_claim",
                incumbentIds,
                reviewedIds,
                "The previous draft relied on administrative context instead of requirements.",
                CancellationToken.None
            }));

        await reviewTask;

        var request = Assert.Single(llm.Requests);
        var requestText = string.Join(
            "\n",
            request.Select(static message => message.Content));
        Assert.Contains(evidenceIds[12], requestText, StringComparison.Ordinal);
        Assert.Contains(evidenceIds[13], requestText, StringComparison.Ordinal);
        Assert.Contains(
            "administrative context",
            requestText,
            StringComparison.OrdinalIgnoreCase);
        Assert.All(incumbentIds, id =>
            Assert.Contains(id, requestText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlatAdequacyTournament_GapReviewsUnseenRetrievedCandidatesBeforeMoreResearch()
    {
        var cards = Enumerable.Range(1, 14)
            .Select(static index => new MealCard(
                $"Documented requirement {index}",
                index))
            .ToArray();
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(cards),
            "Summarize seven documented requirements.");
        var evidenceIds = bundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();
        var reviewedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "partial-pool-gap",
                "submit_flat_evidence_gap",
                new
                {
                    usefulEvidenceIds = new[] { evidenceIds[0], evidenceIds[1] },
                    missingRequirements = new[] { "Five additional requirements" },
                    reason = "Two requirements are useful but the pool is incomplete."
                })),
            Completion(Call(
                "partial-pool-challengers",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[]
                    {
                        evidenceIds[0], evidenceIds[1],
                        evidenceIds[12], evidenceIds[13]
                    },
                    reason = "The retained pool and new challengers are the strongest evidence."
                })));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "ReviewFlatEvidenceAdequacyAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(SourceBackedIntake), typeof(string), typeof(EvidenceBundle),
                typeof(IReadOnlyList<string>), typeof(int), typeof(string),
                typeof(IReadOnlyList<string>), typeof(HashSet<string>),
                typeof(string), typeof(CancellationToken)
            },
            modifiers: null);
        Assert.NotNull(method);
        var arguments = new object?[]
        {
            Intake("Summarize seven documented requirements."),
            "Select the strongest documented requirements.",
            bundle,
            evidenceIds,
            7,
            "content_claim",
            Array.Empty<string>(),
            reviewedIds,
            "The current proof pool is incomplete.",
            CancellationToken.None
        };
        var review = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            arguments));

        await review;

        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(14, reviewedIds.Count);
        var secondRequestText = string.Join(
            "\n",
            llm.Requests[1].Select(static message => message.Content));
        Assert.Contains(evidenceIds[12], secondRequestText, StringComparison.Ordinal);
        Assert.Contains(evidenceIds[13], secondRequestText, StringComparison.Ordinal);
        Assert.Contains(evidenceIds[0], secondRequestText, StringComparison.Ordinal);
        Assert.Contains(evidenceIds[1], secondRequestText, StringComparison.Ordinal);
        var outcome = review.GetType().GetProperty("Result")!.GetValue(review);
        Assert.Equal(
            "select",
            outcome!.GetType().GetProperty("Decision")!.GetValue(outcome));
    }

    [Fact]
    public async Task ContentClaimMode_RevisesTheDraftDirectlyWhenEvidenceRemainsValid()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "ANSI B11.0-2023 exigences",
            topK = 5
        });
        var intake = Intake("Donne deux exigences documentées de ANSI B11.0-2023.") with
        {
            QuestionFocus = "content",
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "deux exigences documentées de ANSI B11.0-2023",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigence documentée",
                    atomicEvidenceMode = "content_claim",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-adequacy",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Les deux exigences couvrent la demande."
                })),
            Completion(
                "Le protecteur réduit l'accès à la zone dangereuse [E2]. "
                + "L'arrêt est disponible pour l'opérateur [E3]."),
            SemanticReview(
                "revise",
                "Les preuves restent valides, mais les verbes doivent reprendre leur portée exacte.",
                preferredAlternativeEvidenceIds: new[] { "E1" }),
            Completion(
                "Le protecteur doit empêcher l'accès à la zone dangereuse [E2]. "
                + "La commande d'arrêt doit être facilement accessible à l'opérateur [E3]."),
            SemanticReview(
                "accept",
                "Chaque exigence est maintenant formulée fidèlement et citée séparément."));
        var executor = new ScriptedToolExecutor(
            DocumentIdentityAndContentSearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { "E2", "E3" }, result.CitedEvidence
            .Select(static evidence => evidence.EvidenceId));
        Assert.Equal(new[] { "rag.search" }, executor.ToolNames);
        Assert.Equal(5, llm.ToolSets.Count);
        Assert.Empty(llm.ToolSets[3]);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.content_claim_revision.returned_to_writer"
            && trace.Fields["selected_evidence"] == "E2,E3");
    }

    [Fact]
    public async Task BoundedNamedDocumentExtraction_CommitsAnExactStructuredRevisionWithoutSecondReview()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "ANSI B11.0-2023 exigences",
            topK = 5
        });
        var intake = Intake("Donne deux exigences documentées de ANSI B11.0-2023.") with
        {
            QuestionFocus = "content",
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "deux exigences documentées de ANSI B11.0-2023",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigence documentée",
                    atomicEvidenceMode = "content_claim",
                    selectionPolicy = "explicit_set",
                    boundedNamedDocumentExtraction = true,
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-adequacy",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Les deux exigences couvrent la demande."
                })),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control shall be readily accessible to the operator.","evidenceIds":["E3"]}]}
                """),
            SemanticReview(
                "revise",
                "Les preuves restent valides, mais les verbes doivent reprendre leur portée exacte."),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone", "evidenceIds":["E2"]},{"text":"The stop control shall be readily accessible to the operator", "evidenceIds":["E3"]}]}
                """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                StructuredFlatWriterEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Equal(new[] { "E2", "E3" }, result.CitedEvidence
            .Select(static evidence => evidence.EvidenceId));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.review_directed_revision.committed_after_source_verification"
            && trace.Fields["selected_evidence"] == "E2,E3");
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed");
    }

    [Fact]
    public async Task BoundedNamedDocumentExtraction_RepairsOneNonExtractiveRevision()
    {
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-adequacy",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Les deux exigences couvrent la demande."
                })),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control shall be readily accessible to the operator.","evidenceIds":["E3"]}]}
                """),
            SemanticReview(
                "revise",
                "Rends uniquement les deux valeurs nommées."),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard improves safety by preventing access.","evidenceIds":["E2"]},{"text":"The stop control supports rapid incident response.","evidenceIds":["E3"]}]}
                """),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control shall be readily accessible to the operator.","evidenceIds":["E3"]}]}
                """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                StructuredFlatWriterEnabled = true
            });

        var result = await runner.RunAsync(
            BoundedNamedValueExtractionIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(5, llm.Requests.Count);
        Assert.Equal(new[] { "E2", "E3" }, result.CitedEvidence
            .Select(static evidence => evidence.EvidenceId));
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.named_value_extraction.protocol_repair.requested");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.named_value_extraction.protocol_repair.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["claim_count"] == "2");
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed");
    }

    [Fact]
    public async Task BoundedNamedDocumentExtraction_StopsAfterOneFailedExtractiveRepair()
    {
        const string invalidRevision =
            """
            {"presentation":"bullets","claims":[{"text":"The guard improves safety by preventing access.","evidenceIds":["E2"]},{"text":"The stop control supports rapid incident response.","evidenceIds":["E3"]}]}
            """;
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-adequacy",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Les deux exigences couvrent la demande."
                })),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control shall be readily accessible to the operator.","evidenceIds":["E3"]}]}
                """),
            SemanticReview("revise", "Rends uniquement les deux valeurs nommées."),
            Completion(invalidRevision),
            Completion(invalidRevision));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                StructuredFlatWriterEnabled = true
            });

        var result = await runner.RunAsync(
            BoundedNamedValueExtractionIntake(),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.Answer);
        Assert.Equal(5, llm.Requests.Count);
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.named_value_extraction.protocol_repair.requested");
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.named_value_extraction.protocol_repair.completed"
            && trace.Fields["protocol_valid"] == "false");
    }

    [Fact]
    public async Task BoundedNamedDocumentExtraction_DoesNotRepairAnotherProtocolError()
    {
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-adequacy",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Les deux exigences couvrent la demande."
                })),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control shall be readily accessible to the operator.","evidenceIds":["E3"]}]}
                """),
            SemanticReview("revise", "Rends uniquement les deux valeurs nommées."),
            Completion("{\"unexpected\":true}"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                StructuredFlatWriterEnabled = true
            });

        var result = await runner.RunAsync(
            BoundedNamedValueExtractionIntake(),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(4, llm.Requests.Count);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.named_value_extraction.protocol_repair.requested");
    }

    [Fact]
    public async Task ContentClaimMode_ReturnsControlToResearchAfterOneFailedDirectRevision()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "safety requirements",
            topK = 4
        });
        var intake = Intake("Donne deux exigences documentées de Safety Standard.") with
        {
            QuestionFocus = "content",
            RequestedDocumentName = "Safety Standard.pdf",
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "deux exigences documentées de Safety Standard",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigence documentée",
                    atomicEvidenceMode = "content_claim",
                    selectionPolicy = "explicit_set",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-proof-pool",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1" },
                    reason = "La fenêtre paraît couvrir les deux exigences."
                })),
            Completion(
                "Le protecteur réduit l'accès à la zone dangereuse [E1]."),
            SemanticReview(
                "revise",
                "Le second claim doit être rédigé à partir de la preuve visible."),
            Completion(
                "Le protecteur empêche l'accès à la zone dangereuse [E1]."),
            SemanticReview(
                "revise",
                "Le pool visible reste trop limité pour couvrir le second claim demandé."),
            Completion(Call(
                "search-inside-visible-document",
                "refine_focused_document_search",
                new
                {
                    documentFocusEvidenceId = "E1",
                    query = "operator stop control requirement",
                    limit = 4
                })));
        var executor = new ScriptedToolExecutor(
            SingleEvidenceWithTwoClaimsSearchResult(),
            SingleEvidenceWithTwoClaimsSearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(new[] { "rag.search", "rag.search" }, executor.ToolNames);
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.content_claim_revision.returned_to_writer");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.content_claim_revision.budget_returned_to_orchestrator"
            && trace.Fields["selected_evidence"] == "E1"
            && trace.Fields["direct_revision_budget"] == "1");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.research_transition.accepted"
            && trace.Fields["decision"] == "search_focused_document"
            && trace.Fields["document_focus_evidence_id"] == "E1");
    }

    [Fact]
    public async Task StructuredFlatWriter_RendersOnlyTheEvidenceAssignmentsAuthoredByTheLlm()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            DocumentIdentityAndContentSearchResult(),
            "Summarize the documented safety requirements.");
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"presentation":"bullets","claims":[{"text":"The guard prevents access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control remains readily accessible to the operator.","evidenceIds":["E3"]}]}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeStructuredFlatWriterAsync(
            runner,
            bundle,
            new[] { "E2", "E3" },
            "content_claim");

        Assert.True(ReadPrivateProperty<bool>(execution, "ProtocolValid"));
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        Assert.Equal(
            "- The guard prevents access to the hazard zone [E2].\n"
            + "- The stop control remains readily accessible to the operator [E3].",
            completion.Content.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.DoesNotContain("claims", completion.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "UNITE_SELECTIONNEE",
            completion.Content,
            StringComparison.Ordinal);
        var contract = Assert.Single(llm.StructuredOutputContracts);
        Assert.Equal("source_backed_flat_writer_v1", contract.Name);
        var evidenceIdEnum = contract.Schema
            .GetProperty("properties")
            .GetProperty("claims")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("evidenceIds")
            .GetProperty("items")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .ToArray();
        Assert.Equal(new[] { "E2", "E3" }, evidenceIdEnum);
    }

    [Fact]
    public async Task StructuredFlatWriter_RendersAMultiSourceClaimWithoutInventingAnotherId()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            DocumentIdentityAndContentSearchResult(),
            "Summarize the documented safety requirements.");
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"presentation":"paragraphs","claims":[{"text":"The safeguards cover both access prevention and an accessible stop control.","evidenceIds":["E2","E3"]}]}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeStructuredFlatWriterAsync(
            runner,
            bundle,
            new[] { "E1", "E2", "E3" },
            "content_claim");

        Assert.True(ReadPrivateProperty<bool>(execution, "ProtocolValid"));
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        Assert.Equal(
            "The safeguards cover both access prevention and an accessible stop control [E2][E3].",
            completion.Content);
        Assert.DoesNotContain("[E1]", completion.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectWriterHandoff_UsesStructuredClaimsBeforeTheNormalVerifier()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "documented machinery safeguards",
            topK = 5
        });
        var intake = Intake("Summarize two documented machinery safeguards.") with
        {
            QuestionFocus = "content",
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "two documented machinery safeguards",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "documented safeguard",
                    atomicEvidenceMode = "content_claim",
                    selectionPolicy = "explicit_set",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "safeguard-selection",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Both excerpts directly document one requested safeguard."
                })),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control shall remain readily accessible to the operator.","evidenceIds":["E3"]}]}
                """),
            SemanticReview(
                "accept",
                "Both claims preserve the source wording and cite their own evidence."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 3,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                StructuredFlatWriterEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(
            result.IsSourceVerified,
            string.Join(
                Environment.NewLine,
                result.TraceEvents.Select(trace =>
                    trace.EventName + " | " + string.Join(
                        ";",
                        trace.Fields.Select(static field =>
                            field.Key + "=" + field.Value)))));
        Assert.Equal(new[] { "E2", "E3" }, result.CitedEvidence
            .Select(static evidence => evidence.EvidenceId));
        Assert.Contains(
            llm.StructuredOutputContracts,
            static contract => contract.Name == "source_backed_flat_writer_v1");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.writer.completed"
            && trace.Fields["finish_reason"] == "structured_flat_writer"
            && trace.Fields["cited_evidence_ids"] == "E2,E3");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.answer.verified"
            && trace.Fields["valid"] == "true"
            && trace.Fields["cited_evidence_ids"] == "E2,E3");
    }

    [Fact]
    public async Task StructuredWriterProtocolFailure_CannotPublishRawControlText()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "documented machinery safeguards",
            topK = 5
        });
        var intake = Intake("Summarize two documented machinery safeguards.") with
        {
            QuestionFocus = "content",
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "two documented machinery safeguards",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "documented safeguard",
                    atomicEvidenceMode = "content_claim",
                    selectionPolicy = "explicit_set",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        const string invalidWriterOutput =
            "{\"presentation\":\"paragraphs\",\"claims\":[{\"text\":\"UNITE_SELECTIONNEE 1 - The guard prevents access.\",\"evidenceIds\":[\"E2\"]}]}";
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "safeguard-selection",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Both excerpts directly document one requested safeguard."
                })),
            Completion(invalidWriterOutput));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 3,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                StructuredFlatWriterEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.Answer);
        Assert.Empty(result.CitedEvidence);
        var writerTrace = Assert.Single(
            result.TraceEvents,
            trace => trace.EventName == "source_backed_agent_v2.writer.completed");
        Assert.Equal("protocol_error", writerTrace.Fields["finish_reason"]);
        Assert.Equal(
            "structured_flat_writer_claim_control_marker",
            writerTrace.Fields["protocol_error"]);
        Assert.Contains(
            "UNITE_SELECTIONNEE 1",
            writerTrace.Fields["protocol_raw_output"],
            StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.answer.verified"
            && trace.Fields["valid"] == "false");
        Assert.DoesNotContain(
            result.TraceEvents,
            trace => trace.EventName == "source_backed_agent_v2.answer.verified"
                     && trace.Fields["valid"] == "true");
    }

    [Fact]
    public async Task StructuredFlatWriter_RejectsAnEvidenceIdOutsideTheVisiblePool()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            DocumentIdentityAndContentSearchResult(),
            "Summarize the documented safety requirements.");
        const string raw =
            "{\"presentation\":\"paragraphs\",\"claims\":[{\"text\":\"The guard prevents access.\",\"evidenceIds\":[\"E99\"]}]}";
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(Completion(raw)),
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeStructuredFlatWriterAsync(
            runner,
            bundle,
            new[] { "E2", "E3" },
            "content_claim");

        Assert.False(ReadPrivateProperty<bool>(execution, "ProtocolValid"));
        Assert.Equal(
            "structured_flat_writer_evidence_id_not_allowed",
            ReadPrivateProperty<string>(execution, "FailureReason"));
        Assert.Equal(raw, ReadPrivateProperty<string>(execution, "RawOutput"));
    }

    [Fact]
    public async Task StructuredFlatWriter_RejectsNonExtractiveNamedValueExpansion()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            DocumentIdentityAndContentSearchResult(),
            "List the two named requirements.");
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"presentation":"bullets","claims":[{"text":"The guard improves operational safety by reducing exposure.","evidenceIds":["E2"]},{"text":"The stop control supports rapid incident response.","evidenceIds":["E3"]}]}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with { StructuredFlatWriterEnabled = true });

        var execution = await InvokeStructuredFlatWriterAsync(
            runner,
            bundle,
            new[] { "E2", "E3" },
            "content_claim",
            requiredClaimCount: 2);

        Assert.False((bool)execution.GetType()
            .GetProperty("ProtocolValid")!.GetValue(execution)!);
        Assert.Equal(
            "structured_flat_writer_named_value_not_extractive",
            execution.GetType().GetProperty("FailureReason")!.GetValue(execution));
    }

    [Theory]
    [InlineData("UNITE_SELECTIONNEE 1 - The guard prevents access.")]
    [InlineData("The guard prevents access [E2].")]
    [InlineData(" ")]
    public async Task StructuredFlatWriter_RejectsControlTextOrModelWrittenCitations(
        string claimText)
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            DocumentIdentityAndContentSearchResult(),
            "Summarize one documented safety requirement.");
        var raw = JsonSerializer.Serialize(new
        {
            presentation = "paragraphs",
            claims = new[]
            {
                new { text = claimText, evidenceIds = new[] { "E2" } }
            }
        });
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(Completion(raw)),
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeStructuredFlatWriterAsync(
            runner,
            bundle,
            new[] { "E2" },
            "named_item");

        Assert.False(ReadPrivateProperty<bool>(execution, "ProtocolValid"));
        Assert.StartsWith(
            "structured_flat_writer_claim_",
            ReadPrivateProperty<string>(execution, "FailureReason"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateAudit_BatchCanApproveAndRejectInOneSemanticDecision()
    {
        var cards = new[]
        {
            new MealCard("Concrete option A", 1),
            new MealCard("Generic collection", 2),
            new MealCard("Concrete option B", 3),
            new MealCard("Schedule position", 4)
        };
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(cards),
            "Give me concrete documented options.");
        var llm = new ScriptedAgentLlm(Completion(
            "{\"decisions\":[1,0,1,0]}"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeCandidateAuditAsync(
            runner,
            bundle.Items);
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");

        Assert.Equal("E1=Concrete option A\r\nE3=Concrete option B", completion.Content);
        Assert.Equal(1, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(4, ReadPrivateProperty<int>(
            execution,
            "CandidateDecisionCount"));
        Assert.Equal(0, ReadPrivateProperty<int>(execution, "ProtocolRepairCount"));
        Assert.All(llm.ToolSets, Assert.Empty);
        Assert.All(llm.RequireToolCalls, Assert.False);
        Assert.All(llm.StructuredOutputContracts, static contract => Assert.Equal(
            "source_backed_candidate_batch_audit_v5",
            contract.Name));
        Assert.All(llm.Temperatures, static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CandidateAudit_UsesCompactOrderedSemanticClassificationsForCanonicalTitles()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Concrete option", 1),
                new MealCard("Generic collection", 2)
            }),
            "Give me concrete documented options.");
        var llm = new ScriptedAgentLlm(Completion(
            "{\"decisions\":[\"accept\",\"reject_category\"]}"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with
            {
                SemanticCandidateLabelResolutionEnabled = true
            });

        var execution = await InvokeCandidateAuditAsync(
            runner,
            bundle.Items,
            semanticPlan:
                "LIVRABLE: concrete documented options\n"
                + "PREUVES_ATOMIQUES: concrete documented options",
            candidateObjectType: string.Empty,
            candidateEligibilityRule: string.Empty);
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");

        Assert.Equal("E1=Concrete option", completion.Content);
        Assert.Equal(1, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(0, ReadPrivateProperty<int>(
            execution,
            "LabelReviewLlmCallCount"));
        Assert.Contains(
            "TYPE ATOMIQUE DECIDE PAR LE LLM: concrete documented options",
            RequestText(llm, 0));
        var contract = Assert.Single(llm.StructuredOutputContracts);
        Assert.Equal("source_backed_resolved_candidate_audit_v5", contract.Name);
        var decisions = contract.Schema
            .GetProperty("properties")
            .GetProperty("decisions");
        Assert.Equal("array", decisions.GetProperty("type").GetString());
        Assert.Equal(2, decisions.GetProperty("minItems").GetInt32());
        Assert.Equal(2, decisions.GetProperty("maxItems").GetInt32());
        Assert.Equal(
            5,
            decisions.GetProperty("items").GetProperty("enum").GetArrayLength());
    }

    [Fact]
    public async Task CandidateAudit_UnresolvedNeighbourDoesNotDowngradeCanonicalTitleBatch()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Concrete option A", 1),
                new MealCard("Concrete option B", 2),
                new MealCard("Unresolved neighbour", 3)
            }),
            "Give me concrete documented options.");
        var mixedCandidates = bundle.Items
            .Select((item, index) => index != 2
                ? item
                : item with
                {
                    SelectionHints = item.SelectionHints
                        .Where(static pair => !string.Equals(
                            pair.Key,
                            "sourceAnchorLabel",
                            StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(
                            static pair => pair.Key,
                            static pair => pair.Value,
                            StringComparer.OrdinalIgnoreCase)
                })
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion("{\"decisions\":[\"accept\",\"accept\"]}"),
            Completion("{\"decisions\":[0]}"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with
            {
                SemanticCandidateLabelResolutionEnabled = true
            });

        var execution = await InvokeCandidateAuditAsync(runner, mixedCandidates);
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");

        Assert.Equal(
            "E1=Concrete option A\r\nE2=Concrete option B",
            completion.Content);
        Assert.Equal(2, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(3, ReadPrivateProperty<int>(
            execution,
            "CandidateDecisionCount"));
        Assert.Equal(
            new[]
            {
                "source_backed_resolved_candidate_audit_v5",
                "source_backed_candidate_batch_audit_v5"
            },
            llm.StructuredOutputContracts.Select(static contract => contract.Name));
    }

    [Fact]
    public async Task CandidateAudit_RechecksIndirectLabelsWithoutChangingSemanticOwnership()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard(
                    "Concrete option A",
                    1,
                    "Generic collection > Concrete option A"),
                new MealCard(
                    "Concrete option B",
                    2,
                    "Generic collection > Concrete option B")
            }),
            "Give me concrete documented options.");
        var unresolvedCandidates = bundle.Items
            .Select(static item => item with
            {
                SelectionHints = item.SelectionHints
                    .Where(static pair => !string.Equals(
                        pair.Key,
                        "sourceAnchorLabel",
                        StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase)
            })
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "{\"labelIndexesByCandidateKey\":{" +
                "\"c1\":0,\"c2\":0}}"),
            Completion(
                "{\"decisionsByCandidateKey\":{" +
                "\"c1\":\"accept::Concrete option A\"," +
                "\"c2\":\"accept::Concrete option B\"}}"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeCandidateAuditAsync(
            runner,
            unresolvedCandidates);
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");

        Assert.Equal(
            "E1=Concrete option A\r\nE2=Concrete option B",
            completion.Content);
        Assert.Equal(2, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(
            1,
            ReadPrivateProperty<int>(execution, "LabelReviewLlmCallCount"));
        Assert.Equal(
            "source_backed_candidate_parent_review_v2",
            llm.StructuredOutputContracts[1].Name);
        Assert.Equal(
            0,
            ReadPrivateProperty<int>(execution, "ProtocolRepairCount"));
        Assert.Contains(
            "CANDIDATS AUX LIBELLES DIRECTS OU PARENTS A REVERIFIER",
            RequestText(llm, 1));
        Assert.Contains("du plus direct au plus ancestral", RequestText(llm, 1));
    }

    [Fact]
    public async Task CandidateAudit_BatchFailsClosedAfterOneProtocolRepair()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Concrete option", 1)
            }),
            "Give me one concrete documented option.");
        var llm = new PromptAwareCandidateAuditLlm(
            static _ => Completion("reponse hors protocole"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());

        var execution = await InvokeCandidateAuditAsync(
            runner,
            bundle.Items);
        var completion = ReadPrivateProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        var decision = ReadPrivateProperty<object>(execution, "Decision");

        Assert.Equal("protocol_error", completion.FinishReason);
        Assert.False(ReadPrivateProperty<bool>(decision, "ProtocolValid"));
        Assert.Equal(
            "candidate_batch_audit_structured_object_required",
            ReadPrivateProperty<string>(decision, "FailureReason"));
        Assert.Equal(2, ReadPrivateProperty<int>(execution, "LlmCallCount"));
        Assert.Equal(1, ReadPrivateProperty<int>(execution, "ProtocolRepairCount"));
        Assert.Contains("REPARATION DU CONTRAT", RequestText(llm, 1));
    }

    [Fact]
    public async Task SplitActionWriter_RequestsLlmSelectionAfterTwoDuplicateOnlyTurns()
    {
        var inventoryCall = Call("cards-1", "documents_content_cards", new
        {
            categoryPath = "Cuisine",
            limit = 20,
            offset = 0
        });
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(inventoryCall),
            Completion(inventoryCall with { Id = "cards-2" }),
            Completion(inventoryCall with { Id = "cards-3" }),
            Completion(SelectionCall(
                "selection-20",
                Enumerable.Range(1, 20)
                    .Select(static index => $"E{index}")
                    .ToArray())),
            SemanticReview(
                "accept",
                "Les vingt valeurs selectionnees sont distinctes et directement soutenues."));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(TwentyMealCards()));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Single(executor.ToolNames);
        Assert.Equal(
            new[] { "submit_evidence_selection" },
            llm.ToolSets[4].Select(static tool => tool.Name).ToArray());
        Assert.True(llm.RequireToolCalls[4]);
        Assert.Contains(
            "BUDGET MECANIQUE SANS PROGRES",
            RequestText(llm, 4),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.documentary_no_progress.selection_requested"
            && trace.Fields["consecutive_no_progress_turns"] == "2");
    }

    [Fact]
    public async Task ContentClaimGap_LaterProtocolFailurePreservesTheLastValidLlmGap()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "safety requirements",
            topK = 7
        });
        var intake = Intake(
            "Summarize seven documented requirements of Safety Standard.") with
        {
            QuestionFocus = "content",
            RequestedDocumentName = "Safety Standard.pdf",
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "seven documented requirements",
                    atomicEvidenceCount = 7,
                    atomicEvidenceType = "documented requirement",
                    atomicEvidenceMode = "content_claim",
                    selectionPolicy = "explicit_set",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ToolAwareContentClaimGapLlm();
        var executor = new ScriptedToolExecutor(
            SevenContentClaimSearchResult(),
            SevenContentClaimSearchResult(startIndex: 8),
            SevenContentClaimSearchResult(startIndex: 8),
            SevenContentClaimSearchResult(startIndex: 8),
            SevenContentClaimSearchResult(startIndex: 8));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 8,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        var resolutionIndex = llm.ToolSets.FindIndex(static tools =>
            tools.Any(static tool => tool.Name == "declare_source_insufficiency"));
        Assert.True(
            resolutionIndex >= 0,
            "Tool history: " + string.Join(
                " => ",
                llm.ToolSets.Select(static tools =>
                    "[" + string.Join(",", tools.Select(static tool => tool.Name)) + "]"))
            + "; traces: "
            + string.Join(
                " => ",
                result.TraceEvents.Select(static trace =>
                    trace.EventName + "{" + string.Join(",", trace.Fields.Select(
                        static field => field.Key + "=" + field.Value)) + "}")));
        Assert.Equal(
            new[]
            {
                "submit_research_action",
                "request_user_clarification",
                "declare_source_insufficiency"
            },
            llm.ToolSets[resolutionIndex]
                .Select(static tool => tool.Name)
                .ToArray());
        Assert.DoesNotContain(
            llm.ToolSets[resolutionIndex],
            static tool => tool.Name == "submit_evidence_selection");
        var resolutionRequest = string.Join(
            Environment.NewLine,
            llm.Requests[resolutionIndex]
                .Select(static message => message.Content));
        Assert.Contains("E1", resolutionRequest);
        Assert.Contains("E2", resolutionRequest);
        Assert.Contains(
            "five requested points remain unsupported",
            resolutionRequest,
            StringComparison.OrdinalIgnoreCase);
        var terminalIndex = llm.ToolSets.FindIndex(static tools =>
            tools.Any(static tool => tool.Name == "resolve_source_yield"));
        Assert.True(terminalIndex > resolutionIndex);
        Assert.Equal(
            new[] { "resolve_source_yield" },
            llm.ToolSets[terminalIndex]
                .Select(static tool => tool.Name)
                .ToArray());
        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            new[]
            {
                "rag.search",
                "rag.search",
                "rag.search"
            },
            executor.ToolNames);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.flat_evidence_adequacy.completed"
            && trace.Fields["protocol_valid"] == "false");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.documentary_no_progress.recovery_requested"
            && trace.Fields["semantic_gap_active"] == "true"
            && trace.Fields["lead_evidence_ids"] == "E1,E2");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.documentary_no_progress.terminal_decision_requested"
            && trace.Fields["consecutive_no_progress_turns"] == "3");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.source_insufficiency.declared"
            && trace.Fields["decision_source"] == "llm_orchestrator");
    }

    [Fact]
    public async Task SplitActionWriter_DoesNotRequestImpossibleSelectionAfterDuplicateLoop()
    {
        var repeated = Call("search-1", "rag_search", new
        {
            query = "options documentees"
        });
        var selectedCards = Enumerable.Range(2, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 instances"),
            Completion(repeated),
            Completion(repeated with { Id = "search-2" }),
            Completion(repeated with { Id = "search-3" }),
            Completion(Call("cards-1", "submit_research_action", new
            {
                capability = "documents_content_cards",
                query = "",
                scope = "Cuisine",
                document = "",
                anchor = "",
                navigationKind = "",
                inventoryMode = "representative",
                limit = 20,
                offset = 0
            })),
            Completion(SelectionCall("selection-20", selectedCards)),
            SemanticReview(
                "accept",
                "Les vingt valeurs selectionnees sont distinctes et directement soutenues."));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            ContentCardInventoryResult(TwentyMealCards()));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt instances documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[]
            {
                "submit_research_action",
                "request_user_clarification",
                "declare_source_insufficiency"
            },
            llm.ToolSets[4].Select(static tool => tool.Name).ToArray());
        Assert.DoesNotContain(
            llm.ToolSets[4],
            static tool => tool.Name == "submit_evidence_selection");
        Assert.Equal(
            new[] { "submit_evidence_selection" },
            llm.ToolSets[5].Select(static tool => tool.Name).ToArray());
        Assert.True(llm.RequireToolCalls[5]);
        Assert.Equal(
            new[] { "rag.search", "documents.content_cards" },
            executor.ToolNames);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.documentary_no_progress.recovery_requested"
            && trace.Fields["observed"] == "1");
    }

    [Fact]
    public async Task SplitActionWriter_NamesDuplicatedEvidenceIdsInSelectionRepair()
    {
        var duplicatedSelection = Enumerable.Range(1, 19)
            .Select(static index => $"E{index}")
            .Concat(new[] { "E1" })
            .ToArray();
        var validSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20
            })),
            Completion(SelectionCall("selection-duplicate", duplicatedSelection)),
            Completion(SelectionCall("selection-valid", validSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs selectionnees sont completes et distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(TwentyMealCards())),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            "selection_evidence_ids_not_unique:E1",
            RequestText(llm, 3),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_rejected"
            && trace.Fields["declared"] == "20"
            && trace.Fields["selection_contract_error"].Contains(
                "E1",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SplitActionWriter_RejectsDifferentIdsForTheSameVisibleSourceBeforeRendering()
    {
        var duplicateVisibleSourceSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var validSelection = new[] { "E1" }
            .Concat(Enumerable.Range(3, 19)
                .Select(static index => $"E{index}"))
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 instances"),
            Completion(Call("context-1", "documents_context", new
            {
                docPath = "Knowledge/options.pdf",
                pageStart = 1,
                pageEnd = 9
            })),
            Completion(Call("context-2", "documents_context", new
            {
                docPath = "Knowledge/options.pdf",
                pageStart = 10,
                pageEnd = 19
            })),
            Completion(Call("context-3", "documents_context", new
            {
                docPath = "Knowledge/options.pdf",
                pageStart = 20,
                pageEnd = 21
            })),
            Completion(SelectionCall(
                "selection-duplicate-source",
                duplicateVisibleSourceSelection)),
            Completion(SelectionCall("selection-valid", validSelection)),
            SemanticReview(
                "accept",
                "Les vingt instances sont distinctes et correctement sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContextResultWithVisibleSourceDuplicate(1, 10),
                ContextResultWithVisibleSourceDuplicate(11, 10),
                ContextResultWithVisibleSourceDuplicate(21, 2)),
            Options() with
            {
                MaximumTurns = 6,
                MaximumObservationItems = 10,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt instances documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            "E2=meme_source_que_E1",
            RequestText(llm, 5),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "groupe_source=V001",
            RequestText(llm, 5),
            StringComparison.OrdinalIgnoreCase);
        var firstSelectionPrompt = RequestText(llm, 4);
        Assert.Contains(
            "- E21 |",
            firstSelectionPrompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "- E2 |",
            firstSelectionPrompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_rejected"
            && trace.Fields["duplicate_visible_source_ids"] == "E2");
        Assert.DoesNotContain("[E2]", result.Answer);
        Assert.Equal(
            20,
            SourceContractVerifier.ExtractEvidenceIds(result.Answer).Count);
    }

    [Fact]
    public async Task SplitActionWriter_RejectsDifferentSourcesWithTheSameFinalDisplayValue()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: grille sourcee\nDIMENSIONS: 2 x 1\n"
                + "PREUVES_ATOMIQUES: 2 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 2 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 2 instances"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 3
            })),
            Completion(Call(
                "selection-duplicate-value",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1", "E2" },
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            Completion(Call(
                "selection-valid",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1", "E3" },
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            SemanticReview(
                "accept",
                "Les deux valeurs finales sont distinctes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Repeated value", 1),
                new MealCard("Repeated value", 2),
                new MealCard("Distinct value", 3)
            })),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 8,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de deux instances documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            "E2=meme_valeur_que_E1(Repeated value)",
            RequestText(llm, 3),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_rejected"
            && trace.Fields["duplicate_display_value_ids"] == "E2");
        Assert.DoesNotContain("[E2]", result.Answer);
        Assert.Contains("Repeated value [E1]", result.Answer);
        Assert.Contains("Distinct value [E3]", result.Answer);
    }

    [Fact]
    public async Task SplitActionWriter_ExtendsOnlyTheProtocolBudgetForAnInvalidFinalSelection()
    {
        var duplicatedSelection = Enumerable.Range(1, 19)
            .Select(static index => $"E{index}")
            .Concat(new[] { "E1" })
            .ToArray();
        var validSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 instances"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 20
            })),
            Completion(SelectionCall("selection-final-invalid", duplicatedSelection)),
            Completion(SelectionCall("selection-protocol-repair", validSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs selectionnees sont completes et distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(TwentyMealCards())),
            Options() with
            {
                MaximumTurns = 2,
                MaximumSemanticCorrectionTurns = 0,
                MaximumSelectionProtocolRepairTurns = 1,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de vingt instances documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(5, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.selection_protocol_repair.extended"
            && trace.Fields["repair_turn"] == "1"
            && trace.Fields["maximum_run_turns"] == "3"
            && trace.Fields["selection_contract_error"].Contains(
                "not_unique",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SplitActionWriter_StopsAfterTwoIdenticalInvalidSelections()
    {
        var duplicatedSelection = Enumerable.Range(1, 19)
            .Select(static index => $"E{index}")
            .Concat(new[] { "E1" })
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 instances"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 20
            })),
            Completion(SelectionCall("selection-invalid-1", duplicatedSelection)),
            Completion(SelectionCall("selection-invalid-2", duplicatedSelection)));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(TwentyMealCards())),
            Options() with
            {
                MaximumTurns = 4,
                MaximumSemanticCorrectionTurns = 0,
                MaximumSelectionProtocolRepairTurns = 2,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de vingt instances documentees."),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.selection_protocol_repeat.stopped"
            && trace.Fields["identical_invalid_selections"] == "2"
            && trace.Fields["selection_contract_error"].Contains(
                "not_unique",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SplitActionWriter_PreservesEvidenceIdsAndExplainsAnInvalidLayout()
    {
        var evidenceIds = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var invalidLayoutCall = Call(
            "selection-invalid-layout",
            "submit_evidence_selection",
            new
            {
                evidenceIds,
                layout = new
                {
                    rowHeader = "Jour",
                    columns = new[]
                    {
                        "Petit-dejeuner",
                        "Dejeuner",
                        "Collation",
                        "Souper"
                    },
                    rows = new[]
                    {
                        new { label = "Lundi" },
                        new { label = "Mardi" },
                        new { label = "Mercredi" },
                        new { label = "Jeudi" },
                        new { label = "Vendredi" }
                    }
                }
            });
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20
            })),
            Completion(invalidLayoutCall),
            Completion(SelectionCall("selection-valid", evidenceIds)),
            SemanticReview(
                "accept",
                "Les vingt valeurs selectionnees sont completes et distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(TwentyMealCards())),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            "selection_layout_rows_invalid",
            RequestText(llm, 3),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "20 entrees EvidenceId",
            RequestText(llm, 3),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_rejected"
            && trace.Fields["declared"] == "20"
            && trace.Fields["selection_arguments"].Contains(
                "\"label\":\"Lundi\"",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task SplitActionWriter_UsesReservedSemanticCorrectionTurnWithoutReopeningRetrieval()
    {
        var cards = TwentyMealCards()
            .Concat(new[] { new MealCard("Recette documentee 21", 21) })
            .ToArray();
        var firstSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var revisedSelection = Enumerable.Range(2, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 21
            })),
            Completion(SelectionCall("selection-first", firstSelection)),
            SemanticReview(
                "revise",
                "E1 doit etre remplace par E21.",
                rejectedEvidenceIds: new[] { "E1" },
                preferredAlternativeEvidenceIds: new[] { "E21" }),
            Completion(SelectionCall("selection-revised", revisedSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs revisees sont completes et distinctes."));
        var executor = new ScriptedToolExecutor(ContentCardInventoryResult(cards));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 2,
                MaximumSemanticCorrectionTurns = 1,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.DoesNotContain("[E1]", result.Answer);
        Assert.Contains("[E21]", result.Answer);
        Assert.Equal(new[] { "documents.content_cards" }, executor.ToolNames);
        Assert.Equal(
            new[] { "submit_evidence_selection" },
            llm.ToolSets[4].Select(static tool => tool.Name).ToArray());
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.started"
            && trace.Fields["maximum_turns"] == "2"
            && trace.Fields["maximum_semantic_correction_turns"] == "1"
            && trace.Fields["maximum_run_turns"] == "3");
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_accepted"
            && trace.Fields["turn"] == "3");
    }

    [Fact]
    public async Task SplitActionWriter_ReopensRetrievalWhenJudgeSaysObservedPoolIsInsufficient()
    {
        var cards = TwentyMealCards();
        var firstSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var revisedSelection = Enumerable.Range(2, 19)
            .Select(static index => $"E{index}")
            .Append("E22")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20
            })),
            Completion(SelectionCall("selection-first", firstSelection)),
            SemanticReview(
                "need_more_evidence",
                "Apres rejet de E1, seulement dix-neuf recettes valides restent visibles.",
                rejectedEvidenceIds: new[] { "E1" }),
            Completion(Call("search-more", "rag_search", new
            {
                query = "nouvelle recette complete",
                categoryPath = "Cuisine"
            })),
            Completion(SelectionCall("selection-revised", revisedSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs sont maintenant completes, distinctes et sourcees."));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(cards),
            SearchResultWithCards("Cuisine/Nouvelles-recettes.pdf", "Cuisine", "new", 42, 1));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 2,
                MaximumSemanticCorrectionTurns = 2,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "documents.content_cards", "rag.search" },
            executor.ToolNames);
        Assert.Contains(llm.ToolSets[4], static tool => tool.Name == "rag_search");
        Assert.Contains(llm.ToolSets[4], static tool => tool.Name == "documents_content_cards");
        Assert.DoesNotContain("[E1]", result.Answer);
        Assert.Contains("[E22]", result.Answer);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "need_more_evidence");
    }

    [Fact]
    public async Task SplitActionWriter_ReservesTwoCompleteSemanticRepairCycles()
    {
        var firstSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var secondSelection = Enumerable.Range(2, 19)
            .Select(static index => $"E{index}")
            .Append("E22")
            .ToArray();
        var finalSelection = Enumerable.Range(3, 18)
            .Select(static index => $"E{index}")
            .Concat(new[] { "E22", "E24" })
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20
            })),
            Completion(SelectionCall("selection-first", firstSelection)),
            SemanticReview(
                "need_more_evidence",
                "E1 n'est pas une recette nommee.",
                rejectedEvidenceIds: new[] { "E1" }),
            Completion(Call("search-first-repair", "rag_search", new
            {
                query = "recette complete supplementaire",
                categoryPath = "Cuisine"
            })),
            Completion(SelectionCall("selection-second", secondSelection)),
            SemanticReview(
                "need_more_evidence",
                "E2 n'est pas une recette nommee.",
                rejectedEvidenceIds: new[] { "E2" }),
            Completion(Call("search-second-repair", "rag_search", new
            {
                query = "autre recette complete",
                categoryPath = "Cuisine"
            })),
            Completion(SelectionCall("selection-final", finalSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs sont maintenant completes, distinctes et sourcees."));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(TwentyMealCards()),
            SearchResultWithCards("Cuisine/Partiel.pdf", "Cuisine", "partial", 41, 1),
            SearchResultWithCards("Cuisine/Final.pdf", "Cuisine", "final", 42, 1));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 2,
                MaximumSemanticCorrectionTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "documents.content_cards", "rag.search", "rag.search" },
            executor.ToolNames);
        Assert.DoesNotContain("[E1]", result.Answer);
        Assert.DoesNotContain("[E2]", result.Answer);
        Assert.Contains("[E22]", result.Answer);
        Assert.Contains("[E24]", result.Answer);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.started"
            && trace.Fields["maximum_run_turns"] == "6");
        Assert.Equal(
            2,
            result.TraceEvents.Count(trace =>
                trace.EventName == "source_backed_agent_v2.semantic_review.completed"
                && trace.Fields["decision"] == "need_more_evidence"));
    }

    [Fact]
    public async Task SplitActionWriter_DoesNotReserveSelectionWhileRejectedPoolHasOnlyNineteenSources()
    {
        var firstSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var finalSelection = Enumerable.Range(3, 18)
            .Select(static index => $"E{index}")
            .Concat(new[] { "E22", "E24" })
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20
            })),
            Completion(SelectionCall("selection-first", firstSelection)),
            SemanticReview(
                "need_more_evidence",
                "Deux valeurs ne sont pas des recettes nommees.",
                rejectedEvidenceIds: new[] { "E1", "E2" }),
            Completion(Call("search-partial", "rag_search", new
            {
                query = "recette complete supplementaire",
                categoryPath = "Cuisine"
            })),
            Completion(Call("search-final", "rag_search", new
            {
                query = "autre recette complete",
                categoryPath = "Cuisine"
            })),
            Completion(SelectionCall("selection-final", finalSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs sont maintenant distinctes et suffisamment prouvees."));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(TwentyMealCards()),
            SearchResultWithCards("Cuisine/Partiel.pdf", "Cuisine", "partial", 41, 1),
            SearchResultWithCards("Cuisine/Final.pdf", "Cuisine", "final", 42, 1));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumSemanticCorrectionTurns = 2,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(llm.ToolSets[5], static tool => tool.Name == "rag_search");
        Assert.Contains(
            llm.ToolSets[5],
            static tool => tool.Name == "documents_content_cards");
        Assert.Equal(
            new[] { "documents.content_cards", "rag.search", "rag.search" },
            executor.ToolNames);
    }

    [Fact]
    public async Task SplitActionWriter_DoesNotReselectEvidenceExplicitlyRejectedByTheLlmJudge()
    {
        var cards = TwentyMealCards()
            .Concat(new[] { new MealCard("Recette documentee 21", 21) })
            .ToArray();
        var firstSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var revisedSelection = Enumerable.Range(2, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 21
            })),
            Completion(SelectionCall("selection-1", firstSelection)),
            SemanticReview(
                "revise",
                "E1 est un libelle generique; E21 est une alternative complete.",
                rejectedEvidenceIds: new[] { "E1" },
                preferredAlternativeEvidenceIds: new[] { "E21" }),
            Completion(SelectionCall("selection-repeated", firstSelection)),
            Completion(SelectionCall("selection-2", revisedSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs revisees sont completes.",
                rejectedEvidenceIds: Array.Empty<string>(),
                preferredAlternativeEvidenceIds: Array.Empty<string>()));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumTurns = 6,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.DoesNotContain("[E1]", result.Answer);
        Assert.Contains("[E21]", result.Answer);
        Assert.Contains(
            "EvidenceId explicitement rejetes par le juge LLM precedent: E1",
            RequestText(llm, 5),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_rejected"
            && trace.Fields["semantically_rejected_evidence_ids"] == "E1");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["rejected_evidence_ids"] == "E1"
            && trace.Fields["preferred_alternative_evidence_ids"] == "E21");
    }

    [Fact]
    public async Task SplitActionWriter_ReturnsToolsToTheLlmAfterAnIdenticalRejectedRevision()
    {
        const string repeatedDraft = "Un composant incomplet [E2].";
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option concrete et sourcee."),
            Completion(Call("first", "rag_search", new { query = "option initiale" })),
            Completion("PRET_A_REDIGER: une preuve est visible."),
            Completion(repeatedDraft),
            SemanticReview("revise", "Le composant reste generique."),
            Completion("PRET_A_REDIGER: reviser avec la preuve visible."),
            Completion(repeatedDraft),
            SemanticReview("revise", "Le composant reste generique."),
            Completion(Call("second", "rag_search", new { query = "option complete" })),
            Completion("PRET_A_REDIGER: une option complete est maintenant visible."),
            Completion("L'option complete new-card-1 est disponible [E4]."),
            SemanticReview("accept", "E4 soutient une option complete et utilisable."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Knowledge/Initial.pdf", "Knowledge", "old", 2, 1),
            SearchResultWithCards("Knowledge/Complete.pdf", "Knowledge", "new", 4, 1));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 7,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains(
            "REVISION SANS PROGRES DETECTEE MECANIQUEMENT",
            RequestText(llm, 8));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
            == "source_backed_agent_v2.semantic_revision.no_progress_returned_to_orchestrator");
    }

    [Fact]
    public async Task SemanticJudge_CanRejectADraftAndReturnControlToTheToolUsingOrchestrator()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option concrete et correctement sourcee."),
            Completion(Call("first", "rag_search", new { query = "option initiale" })),
            Completion("Un composant incomplet [E2]."),
            SemanticReview("need_more_evidence", "E2 est un composant, pas une option complete."),
            Completion(Call("second", "rag_search", new { query = "option complete" })),
            Completion("L'option complete new-card-1 est disponible [E4]."),
            SemanticReview("accept", "E4 soutient une option complete et utilisable."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Knowledge/Initial.pdf", "Knowledge", "old", 2, 1),
            SearchResultWithCards("Knowledge/Complete.pdf", "Knowledge", "new", 4, 1));
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("new-card-1", result.Answer);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains("rejectedEvidenceIds", RequestText(llm, 3));
        Assert.True(llm.RequireToolCalls[3]);
        var reviewTool = Assert.Single(
            llm.ToolSets[3],
            static tool => tool.Name == "submit_semantic_review");
        var reviewProperties = reviewTool.Parameters.GetProperty("properties");
        Assert.True(reviewProperties.TryGetProperty("rejectedEvidenceIds", out _));
        Assert.True(reviewProperties.TryGetProperty("preferredAlternativeEvidenceIds", out _));
        Assert.Contains("REVUE SEMANTIQUE INDEPENDANTE", RequestText(llm, 4));
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "need_more_evidence");
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
    }

    [Fact]
    public async Task SemanticJudge_ReopensToolsWhenARevisionRejectsEvidenceWithoutEnoughNamedReplacements()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option concrete et correctement sourcee."),
            Completion(Call("first", "rag_search", new { query = "option initiale" })),
            Completion("Un composant incomplet [E2]."),
            SemanticReview(
                "revise",
                "E2 est un composant invalide qui doit etre remplace.",
                rejectedEvidenceIds: new[] { "E2" },
                preferredAlternativeEvidenceIds: Array.Empty<string>()),
            Completion(Call("second", "rag_search", new { query = "option complete" })),
            Completion("L'option complete new-card-1 est disponible [E4]."),
            SemanticReview("accept", "E4 soutient une option complete et utilisable."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Knowledge/Initial.pdf", "Knowledge", "old", 2, 1),
            SearchResultWithCards("Knowledge/Complete.pdf", "Knowledge", "new", 4, 1));
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("new-card-1", result.Answer);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains(
            "Revision non soutenue",
            RequestText(llm, 4),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "need_more_evidence"
            && trace.Fields["contract_adjusted"] == "true"
            && trace.Fields["rejected_evidence_count"] == "1"
            && trace.Fields["preferred_alternative_count"] == "0");
    }

    [Fact]
    public async Task SemanticJudge_AllowsOneReplacementForSeveralProofFragmentsOfOneValue()
    {
        var cards = new[]
        {
            new MealCard("Fragment provisoire A", 1),
            new MealCard("Fragment provisoire B", 2),
            new MealCard("Option complete", 3)
        };
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: une option\nDIMENSIONS: aucune\n"
                + "PREUVES_ATOMIQUES: 1 option complete\n"
                + "INTENTIONS_RECHERCHE: option\n"
                + "APPROCHE_OUTILS: documents_content_cards\n"
                + "PREMIERE_ACTION: aucune\n"
                + "ACCEPTER_SI: une option sourcee\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune option"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 3
            })),
            Completion("Option provisoire [E1] [E2]."),
            SemanticReview(
                "revise",
                "Les deux fragments soutiennent une seule valeur invalide; E3 la remplace.",
                rejectedEvidenceIds: new[] { "E1", "E2" },
                preferredAlternativeEvidenceIds: new[] { "E3" }),
            Completion("L'option complete est disponible [E3]."),
            SemanticReview(
                "accept",
                "E3 soutient une option complete et utilisable."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options());

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("[E3]", result.Answer);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "revise"
            && trace.Fields["contract_adjusted"] == "false"
            && trace.Fields["rejected_evidence_count"] == "2"
            && trace.Fields["preferred_alternative_count"] == "1");
    }

    [Fact]
    public async Task SemanticJudge_RetriesOnceWithMoreTokensWhenTheToolCallIsTruncated()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option concrete et correctement sourcee."),
            Completion(Call("first", "rag_search", new { query = "option complete" })),
            Completion("L'option complete est disponible [E1]."),
            new SourceBackedAgentCompletion(
                string.Empty,
                Array.Empty<SourceBackedAgentToolCall>(),
                "length",
                200,
                320),
            SemanticReview(
                "accept",
                "Une instance complete, distincte et utilisable a ete verifiee: E1."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                MaximumSemanticReviewTokens = 320
            });

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(320, llm.MaxTokens[3]);
        Assert.Equal(768, llm.MaxTokens[4]);
        Assert.Contains(
            "REPARATION DE PROTOCOLE",
            RequestText(llm, 4),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["attempts"] == "2"
            && trace.Fields["truncation_retry_exhausted"] == "false");
    }

    [Fact]
    public async Task SemanticJudge_RejectsAnUnjustifiedAcceptAndReturnsControlToTheLlm()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option concrete et correctement sourcee."),
            Completion(Call("first", "rag_search", new { query = "option initiale" })),
            Completion("Un composant incomplet [E2]."),
            Completion("""{"decision":"accept","reasons":[]}"""),
            Completion(Call("second", "rag_search", new { query = "option complete" })),
            Completion("L'option complete new-card-1 est disponible [E4]."),
            SemanticReview(
                "accept",
                "Une instance complete, distincte et utilisable a ete verifiee: E4."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Knowledge/Initial.pdf", "Knowledge", "old", 2, 1),
            SearchResultWithCards("Knowledge/Complete.pdf", "Knowledge", "new", 4, 1));
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "need_more_evidence"
            && trace.Fields["reasons"].Contains(
                "aucune justification",
                StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public async Task NativeLoop_LetsLlmChooseContentCardInventoryAndCiteItsStablePage()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: selection de recettes distinctes et sourcees."),
            Completion(Call("cards-1", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                q = "recette",
                limit = 60
            })),
            Completion("Je retiens les Sticks de feta comme recette documentee [E1]."),
            SemanticReview("accept", "E1 nomme la recette et fournit un ancrage documentaire stable."));
        var executor = new ScriptedToolExecutor(ContentCardInventoryResult());
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Propose une recette documentee provenant de la categorie Cuisine."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { "documents.content_cards" }, executor.ToolNames);
        var evidence = Assert.Single(result.CitedEvidence);
        Assert.Equal("canonical_content_card", evidence.SourceKind);
        Assert.Equal("Cuisine/chefbot_livre_de_recettes_fr.pdf", evidence.DocPath);
        Assert.Equal(21, evidence.PageStart);
        Assert.Equal("card-feta", evidence.ContentCardId);
        Assert.Null(evidence.ChunkId);
        Assert.Contains("Sticks de feta", evidence.Excerpt);
        Assert.Contains("20 pieces", evidence.Excerpt);
        Assert.Contains("E1 | groupe_source=", RequestText(llm, 2));
        Assert.Contains("| carte | valeur=Sticks de feta", RequestText(llm, 2));
    }

    [Fact]
    public async Task SemanticJudge_ReceivesTheCompleteTwentyCellDraftAndEveryCitedCard()
    {
        var cards = TwentyMealCards();
        var draft = string.Join(
            Environment.NewLine,
            cards.Select((card, index) => $"{card.Title} [E{index + 1}]"));
        Assert.True(draft.Length > 2600);
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning de repas\nDIMENSIONS: 5 jours x 4 moments\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nACCEPTER_SI: 20 cellules citees\n"
                + "INSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards-20", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 40
            })),
            Completion(draft),
            SemanticReview("accept", "Les vingt recettes distinctes et citees sont toutes visibles."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with { MaximumWorkingEvidenceItems = 40 });

        var result = await runner.RunAsync(
            Intake(
                "Fais un planning du lundi au vendredi avec petit-dejeuner, dejeuner, collation et souper."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var judgeRequest = RequestText(llm, 3);
        Assert.Contains("Vendredi-Souper", judgeRequest);
        Assert.Contains("E20 | valeur=Recette 20", judgeRequest);
        Assert.Contains("fichier=recettes.pdf", judgeRequest);
        Assert.True(llm.RequireToolCalls[3]);
        Assert.Contains(
            "axes, jours, colonnes et affectations",
            judgeRequest,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContextOverflow_RecompressesStateAndRetriesWithoutChangingSemanticOwnership()
    {
        var cards = TwentyMealCards();
        var evidenceIds = Enumerable.Range(1, 20)
            .Select(static index => "E" + index)
            .ToArray();
        var llm = new ContextOverflowOnceLlm(
            Completion(
                "LIVRABLE: planning de repas\n"
                + "DIMENSIONS: 5 jours x 4 moments\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\n"
                + "APPROCHE_OUTILS: documents_content_cards puis decision selon les preuves\n"
                + "PREMIERE_ACTION: documents_content_cards {\"categoryPath\":\"Cuisine\",\"limit\":40,\"offset\":0}\n"
                + "ACCEPTER_SI: 20 recettes distinctes et citees\n"
                + "INSUFFISANT_SEULEMENT_SI: moins de 20 recettes utilisables"),
            new HttpRequestException(
                "LLM request failed: 400 Bad Request. Body: request (4204 tokens) exceeds the available context size (4096 tokens)",
                null,
                System.Net.HttpStatusCode.BadRequest),
            Completion("PRET_A_REDIGER: les preuves sont suffisantes."),
            Completion(SelectionCall("selection-after-recovery", evidenceIds)),
            SemanticReview(
                "accept",
                "Les vingt recettes distinctes et citees couvrent les vingt cellules."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                SeparateActionAndWriter = true,
                MaximumWorkingEvidenceItems = 40,
                MaximumContextTokens = 4096
            });

        var result = await runner.RunAsync(
            Intake(
                "Fais un planning du lundi au vendredi avec petit-dejeuner, dejeuner, collation et souper."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            result.TraceEvents,
            static trace => string.Equals(
                trace.EventName,
                "source_backed_agent_v2.context.recovery_requested",
                StringComparison.Ordinal));
        var failedRequest = string.Join(
            Environment.NewLine,
            llm.Requests[1].Select(static message => message.Content));
        var recoveryRequest = string.Join(
            Environment.NewLine,
            llm.Requests[2].Select(static message => message.Content));
        Assert.Contains("RECUPERATION MECANIQUE DU CONTEXTE", recoveryRequest);
        Assert.True(
            recoveryRequest.Length < failedRequest.Length,
            $"Recovery request should be smaller ({recoveryRequest.Length} >= {failedRequest.Length}).");
        Assert.Contains(
            llm.ToolSets[2],
            static tool => string.Equals(
                tool.Name,
                "documents_content_cards",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResearchTransition_PreflightRecoveryUsesItsOwnCompactSemanticPrompt()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "pump HX-42 requirements",
            topK = 4
        });
        var intake = Intake("Summarize four documented requirements of pump HX-42.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "four documented requirements",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "documented requirement",
                    initialCapability = "rag_search",
                    rowHeader = "Group",
                    rowLabels = new[] { "A", "B" },
                    columns = new[] { "Requirement 1", "Requirement 2" }
                }),
                "llm_router")
        };
        var llm = new TokenCountingScriptedAgentLlm(
            new[] { 2000, 2000, 5000, 2000 },
            Completion(CandidateAuditCall(
                "audit-search",
                new[] { "E1" },
                Array.Empty<string>())),
            Completion(Call(
                "focused-search-after-recovery",
                "refine_focused_document_search",
                new
                {
                    documentFocusEvidenceId = "E1",
                    query = "operator protection requirements",
                    limit = 4
                })));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 3,
                MaximumContextTokens = 4096,
                SeparateActionAndWriter = false,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateStrategyEnabled = false
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(new[] { 2000, 2000, 5000, 2000 }, llm.InputTokenCountsReturned);
        Assert.Equal(2, llm.CompleteCallCount);
        var recoveryRequest = string.Join(
            Environment.NewLine,
            llm.CountedRequests[3].Select(static message => message.Content));
        Assert.True(
            recoveryRequest.Contains(
                "CONTEXTE COMPACT DE RECUPERATION",
                StringComparison.Ordinal),
            "Counted tool sets: " + string.Join(
                " || ",
                llm.CountedToolSets.Select(static tools => string.Join(
                    ",",
                    tools.Select(static tool => tool.Name)))));
        Assert.Contains(
            "Summarize four documented requirements",
            recoveryRequest,
            StringComparison.Ordinal);
        Assert.Contains(
            "E1 | document=Manuals/HydraulicPumpManual.pdf",
            recoveryRequest,
            StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.context.recovery_requested"
            && trace.Fields["reason"] == "preflight_exact_token_budget");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.research_transition.accepted"
            && trace.Fields["decision"] == "search_focused_document");
    }

    [Fact]
    public async Task ExactRuntimeTokenCount_RecompressesBeforeTheGenerationRequestOverflows()
    {
        var llm = new ExactTokenCountLlm(
            new[] { 5000, 2000, 2000 },
            Completion(
                "LIVRABLE: reponse factuelle sourcee\n"
                + "PREUVES_ATOMIQUES: 1 passage\n"
                + "PREMIERE_ACTION: rag_search {\"query\":\"couple serrage HX-42\"}"),
            Completion("Le couple de serrage est de 85 NÂ·m [E1]."),
            SemanticReview("accept", "E1 contient directement la valeur demandee."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                MaximumContextTokens = 4096,
                MaximumOutputTokens = 900
            });

        var result = await runner.RunAsync(
            Intake("Quel est le couple de serrage de la pompe HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { 5000, 2000, 2000 }, llm.InputTokenCountsReturned);
        var recovery = Assert.Single(
            result.TraceEvents,
            static trace => string.Equals(
                trace.EventName,
                "source_backed_agent_v2.context.recovery_requested",
                StringComparison.Ordinal));
        Assert.Equal(
            "preflight_exact_token_budget",
            recovery.Fields["reason"]);
        var completedTurn = Assert.Single(
            result.TraceEvents,
            static trace => string.Equals(
                                trace.EventName,
                                "source_backed_agent_v2.turn.completed",
                                StringComparison.Ordinal)
                            && trace.Fields.GetValueOrDefault("input_tokens_exact") == "2000");
        Assert.NotNull(completedTurn);
    }

    [Fact]
    public async Task ExactRuntimeTokenCount_CompactsSemanticReviewBeforeSendingIt()
    {
        var llm = new ExactTokenCountLlm(
            new[] { 2000, 8000, 3000 },
            Completion(
                "LIVRABLE: reponse factuelle sourcee\n"
                + "PREUVES_ATOMIQUES: 1 passage\n"
                + "PREMIERE_ACTION: rag_search {\"query\":\"couple serrage HX-42\"}"),
            Completion("Le couple de serrage est de 85 NÃ‚Â·m [E1]."),
            SemanticReview("accept", "E1 contient directement la valeur demandee."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                MaximumContextTokens = 8192,
                MaximumSemanticReviewTokens = 640
            });

        var result = await runner.RunAsync(
            Intake("Quel est le couple de serrage de la pompe HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { 2000, 8000, 3000 },
            llm.InputTokenCountsReturned);
        Assert.Equal(3, llm.CompleteCallCount);
        Assert.Contains(
            result.TraceEvents,
            static trace =>
                trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
                && trace.Fields["context_recovery_used"] == "true");
    }

    [Fact]
    public async Task ExactRuntimeTokenCount_NeverSendsARequestKnownOutsideContextAfterRecovery()
    {
        var llm = new ExactTokenCountLlm(
            new[] { 5000, 4500 },
            Completion(
                "LIVRABLE: reponse factuelle sourcee\n"
                + "PREUVES_ATOMIQUES: 1 passage\n"
                + "PREMIERE_ACTION: aucune"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with
            {
                MaximumContextTokens = 4096,
                MaximumOutputTokens = 900
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(
                Intake("Quel est le couple de serrage de la pompe HX-42 ?"),
                CancellationToken.None));

        Assert.Contains(
            "outside the measured context budget",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { 5000, 4500 }, llm.InputTokenCountsReturned);
        Assert.Equal(1, llm.CompleteCallCount);
    }

    [Fact]
    public void ContentCardObservation_UsesWorkingEvidenceCapacityWithoutHidingReturnedPageItems()
    {
        var cards = Enumerable.Range(1, 5)
            .Select(index => new
            {
                docId = "doc-recettes",
                docName = "recettes.pdf",
                docPath = "Cuisine/recettes.pdf",
                categoryPath = "Cuisine",
                contentCardId = "card-" + index,
                title = "Recette " + index,
                pageStart = index,
                pageEnd = index,
                hasGroundedEvidence = true
            })
            .ToArray();
        var results = Results(
            "documents.content_cards",
            new
            {
                citable = true,
                total = 5,
                limit = 5,
                offset = 0,
                nextOffset = 5,
                items = cards
            });
        var bundle = EvidenceBundleBuilder.FromToolResults(
            results,
            "Donne plusieurs recettes.");
        var options = Options() with
        {
            MaximumObservationItems = 2,
            MaximumWorkingEvidenceItems = 4,
            MaximumWorkingExcerptCharacters = 80
        };

        using var compact = JsonDocument.Parse(SourceBackedAgentObservationCompactor.Build(
            "documents_content_cards",
            results.Items,
            bundle,
            1,
            options));

        Assert.Equal(4, compact.RootElement.GetProperty("shownEvidenceCount").GetInt32());
        Assert.Equal(5, compact.RootElement.GetProperty("availableEvidenceCount").GetInt32());
        Assert.True(compact.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(4, compact.RootElement.GetProperty("evidence").GetArrayLength());
        Assert.Equal(
            5,
            SourceBackedAgentObservationCompactor.ReadNextOffset(results.Items));
    }

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

    [Fact]
    public void EvidenceBundle_DeduplicatesSameStableContentCardAcrossInventoryAndSearch()
    {
        var inventory = ContentCardInventoryResult();
        var search = Results(
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "doc-recettes",
                        docName = "chefbot_livre_de_recettes_fr.pdf",
                        docPath = "Cuisine/chefbot_livre_de_recettes_fr.pdf",
                        categoryPath = "Cuisine",
                        pageStart = 21,
                        pageEnd = 21,
                        chunkId = "search-chunk-21",
                        excerpt = "Page contenant la recette.",
                        matchedContentCards = new[]
                        {
                            new
                            {
                                contentCardId = "card-feta",
                                title = "Sticks de feta",
                                kind = "page_embedded_title",
                                profileVersion = "foundation_v1",
                                hasGroundedEvidence = true,
                                pageStart = 21,
                                pageEnd = 21,
                                evidence = new
                                {
                                    facts = new[]
                                    {
                                        new { sourceText = "20 pieces" }
                                    }
                                }
                            }
                        }
                    }
                }
            });
        var cumulative = new ToolResults();
        cumulative.Items.AddRange(inventory.Items);
        cumulative.Items.AddRange(search.Items);

        var bundle = EvidenceBundleBuilder.FromToolResults(
            cumulative,
            "Donne une recette.",
            materializeMatchedContentCards: true);

        var card = Assert.Single(bundle.Items, static item =>
            item.SourceKind == "canonical_content_card");
        Assert.Equal("card-feta", card.ContentCardId);
        Assert.Null(card.ChunkId);
        Assert.Contains("20 pieces", card.Excerpt);
        Assert.Equal("page_embedded_title", card.SelectionHints["kind"]);
        Assert.Equal(
            "true",
            card.SelectionHints["hasGroundedEvidence"],
            ignoreCase: true);
    }

    [Theory]
    [InlineData("""{"query":"recette","docId":"D1","docPath":"Cuisine/recettes.pdf"}""", null, "Cuisine/recettes.pdf")]
    [InlineData("""{"query":"recette","docId":"Cuisine/recettes.pdf"}""", null, "Cuisine/recettes.pdf")]
    [InlineData("""{"query":"recette","docId":"doc-123"}""", "doc-123", null)]
    public void ToolArgumentNormalizer_SeparatesOpaqueDocumentIdsFromPathsAndReadOnlyAliases(
        string json,
        string? expectedDocId,
        string? expectedDocPath)
    {
        using var input = JsonDocument.Parse(json);

        var normalized =
            SourceBackedAgentToolArgumentNormalizer.NormalizeDocumentLocator(input.RootElement);

        Assert.Equal("recette", normalized.GetProperty("query").GetString());
        Assert.Equal(
            expectedDocId,
            normalized.TryGetProperty("docId", out var docId)
                ? docId.GetString()
                : null);
        Assert.Equal(
            expectedDocPath,
            normalized.TryGetProperty("docPath", out var docPath)
                ? docPath.GetString()
                : null);
    }

    [Theory]
    [InlineData("""{"docRef":"FIT-PTFE_TF_1620-EN.pdf"}""")]
    [InlineData("""{"docPath":"FIT-PTFE_TF_1620-EN.pdf"}""")]
    [InlineData("""{"docId":"FIT-PTFE_TF_1620-EN.pdf"}""")]
    public void ToolArgumentNormalizer_PreservesBareFilenameAsResolvableReference(
        string json)
    {
        using var input = JsonDocument.Parse(json);

        var normalized =
            SourceBackedAgentToolArgumentNormalizer.NormalizeDocumentLocator(
                input.RootElement);

        Assert.Equal(
            "FIT-PTFE_TF_1620-EN.pdf",
            normalized.GetProperty("docRef").GetString());
        Assert.False(normalized.TryGetProperty("docPath", out _));
        Assert.False(normalized.TryGetProperty("docId", out _));
    }

    [Fact]
    public void ToolArgumentNormalizer_ResolvesPromptAliasesThroughCanonicalEvidence()
    {
        var item = new EvidenceItem(
            "E5",
            "rag_hit",
            "rag.search",
            "PTFE",
            "backend-doc-42",
            "FIT-PTFE_TF_1620-EN.pdf",
            "Documents techniques/FIT-PTFE_TF_1620-EN.pdf",
            null,
            null,
            1,
            1,
            "backend-chunk-9",
            "Product Form and Packaging",
            "product form and packaging",
            0.9,
            1,
            "Documents techniques",
            "en",
            "en",
            "high",
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
        var bundle = new EvidenceBundle(
            "bundle-1",
            "question",
            new[] { item },
            Array.Empty<SourceBackedTraceEvent>());
        using var input = JsonDocument.Parse(
            """{"docRef":"D2","docId":"E5","chunkId":"chunk-3","pageStart":9,"pageEnd":9}""");

        var normalized =
            SourceBackedAgentToolArgumentNormalizer.NormalizeDocumentLocator(
                input.RootElement,
                bundle);

        Assert.False(normalized.TryGetProperty("docRef", out _));
        Assert.Equal(
            "backend-doc-42",
            normalized.GetProperty("docId").GetString());
        Assert.Equal(
            "Documents techniques/FIT-PTFE_TF_1620-EN.pdf",
            normalized.GetProperty("docPath").GetString());
        Assert.Equal(
            "backend-chunk-9",
            normalized.GetProperty("chunkId").GetString());
        Assert.Equal(1, normalized.GetProperty("pageStart").GetInt32());
        Assert.Equal(1, normalized.GetProperty("pageEnd").GetInt32());
    }

    [Fact]
    public void ToolArgumentNormalizer_PreservesConcreteDocumentPathOverConflictingEvidenceAlias()
    {
        var wrongEvidence = new EvidenceItem(
            "E5",
            "rag_hit",
            "rag.search",
            "PTFE certificate",
            "wrong-doc",
            "certificate.pdf",
            "Technical/Certificates/certificate.pdf",
            null,
            null,
            1,
            1,
            "wrong-chunk",
            null,
            null,
            0.9,
            1,
            "Technical/Certificates",
            "en",
            "en",
            "high",
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
        var bundle = new EvidenceBundle(
            "bundle-1",
            "question",
            new[] { wrongEvidence },
            Array.Empty<SourceBackedTraceEvent>());
        using var input = JsonDocument.Parse(
            """
            {
              "docRef":"Technical/Data sheets/FIT.pdf",
              "docId":"E5",
              "chunkId":"wrong-chunk",
              "pageStart":1,
              "pageEnd":1
            }
            """);

        var normalized =
            SourceBackedAgentToolArgumentNormalizer.NormalizeDocumentLocator(
                input.RootElement,
                bundle);

        Assert.Equal(
            "Technical/Data sheets/FIT.pdf",
            normalized.GetProperty("docPath").GetString());
        Assert.Equal(
            "Technical/Data sheets/FIT.pdf",
            normalized.GetProperty("docRef").GetString());
        Assert.False(normalized.TryGetProperty("docId", out _));
        Assert.False(normalized.TryGetProperty("chunkId", out _));
        Assert.Equal(1, normalized.GetProperty("pageStart").GetInt32());
    }

    [Fact]
    public async Task OpenAiClient_SerializesNativeToolHistoryAndParsesToolCalls()
    {
        var requestBodies = new List<string>();
        var handler = new DelegateHandler(async request =>
        {
            requestBodies.Add(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [{
                        "finish_reason": "tool_calls",
                        "message": {
                          "content": null,
                          "tool_calls": [{
                            "id": "call-42",
                            "type": "function",
                            "function": {
                              "name": "rag_search",
                              "arguments": "{\"query\":\"HX-42 torque\"}"
                            }
                          }]
                        }
                      }],
                      "usage": { "prompt_tokens": 120, "completion_tokens": 18 },
                      "timings": {
                        "cache_n": 119,
                        "prompt_n": 1,
                        "prompt_ms": 12.5,
                        "predicted_n": 18,
                        "predicted_ms": 2400.25
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = new OpenAiLlmClient(new HttpClient(handler));
        client.Configure("http://localhost:12662/v1", "qwen3");

        var completion = await client.ChatOnceNativeAsync(
            new[]
            {
                SourceBackedAgentMessage.System("orchestrate"),
                SourceBackedAgentMessage.User("question")
            },
            SourceBackedAgentToolCatalog.Build(),
            temperature: 0.7,
            maxTokens: 900,
            CancellationToken.None);

        Assert.Equal("tool_calls", completion.FinishReason);
        var call = Assert.Single(completion.ToolCalls);
        Assert.Equal("call-42", call.Id);
        Assert.Equal("rag_search", call.Name);
        Assert.Equal("HX-42 torque", call.Arguments.GetProperty("query").GetString());
        Assert.Equal(120, completion.PromptTokens);
        Assert.Equal(119, completion.ServerCacheTokens);
        Assert.Equal(1, completion.ServerPromptTokensEvaluated);
        Assert.Equal(12.5, completion.ServerPromptMilliseconds);
        Assert.Equal(18, completion.ServerPredictedTokens);
        Assert.Equal(2400.25, completion.ServerPredictedMilliseconds);
        Assert.Contains("\"tool_choice\":\"auto\"", requestBodies[0]);
        Assert.Contains("\"parallel_tool_calls\":true", requestBodies[0]);
        Assert.Contains("\"name\":\"documents_navigation\"", requestBodies[0]);

        await client.ChatOnceNativeAsync(
            new[] { SourceBackedAgentMessage.User("audit") },
            new[]
            {
                new SourceBackedAgentToolDefinition(
                    "submit_semantic_review",
                    "Submit the semantic review.",
                    JsonSerializer.SerializeToElement(new
                    {
                        type = "object",
                        properties = new { decision = new { type = "string" } },
                        required = new[] { "decision" }
                    }))
            },
            temperature: 0,
            maxTokens: 128,
            CancellationToken.None,
            requireToolCall: true);

        Assert.Contains(
            "\"tool_choice\":\"required\"",
            requestBodies[1]);
        Assert.Contains("\"parallel_tool_calls\":false", requestBodies[1]);
        using var requiredToolPayload = JsonDocument.Parse(requestBodies[1]);
        Assert.Equal(
            0d,
            requiredToolPayload.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(
            1d,
            requiredToolPayload.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(
            0d,
            requiredToolPayload.RootElement.GetProperty("frequency_penalty").GetDouble());
        Assert.Equal(
            0d,
            requiredToolPayload.RootElement.GetProperty("presence_penalty").GetDouble());
    }

    private static string RequestText(ScriptedAgentLlm llm, int requestIndex)
        => string.Join(
            Environment.NewLine,
            llm.Requests[requestIndex].Select(static message => message.Content));

    private static string RequestText(ContextOverflowOnceLlm llm, int requestIndex)
        => string.Join(
            Environment.NewLine,
            llm.Requests[requestIndex].Select(static message => message.Content));

    private static string RequestText(
        PromptAwareCandidateAuditLlm llm,
        int requestIndex)
        => string.Join(
            Environment.NewLine,
            llm.Requests[requestIndex].Select(static message => message.Content));

    private static void AssertSemanticReviewTool(
        IReadOnlyList<SourceBackedAgentToolDefinition> tools)
        => Assert.Equal(
            "submit_semantic_review",
            Assert.Single(tools).Name);

    private static SourceBackedAgentV2Options Options()
        => new(
            MaximumTurns: 6,
            MaximumToolCalls: 8,
            MaximumObservationItems: 8,
            MaximumObservationExcerptCharacters: 360,
            MaximumOutputTokens: 900,
            SeparateActionAndWriter: false,
            MaximumSemanticCorrectionTurns: 0,
            SemanticCandidateAuditEnabled: false,
            SemanticColumnRoleReviewEnabled: false,
            MaximumSelectionProtocolRepairTurns: 0,
            StructuredSemanticPlanningEnabled: false);

    private static SourceBackedIntake Intake(string question)
        => new(
            question,
            "rag.answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: "fr");

    private static SourceBackedIntake BoundedNamedValueExtractionIntake()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "ANSI B11.0-2023 exigences",
            topK = 5
        });
        return Intake("Donne deux exigences documentées de ANSI B11.0-2023.") with
        {
            QuestionFocus = "content",
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "deux exigences documentées de ANSI B11.0-2023",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigence documentée",
                    atomicEvidenceMode = "content_claim",
                    selectionPolicy = "explicit_set",
                    boundedNamedDocumentExtraction = true,
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
    }

    private static async Task<(
        SourceBackedPipelineResult Result,
        ScriptedAgentLlm Llm,
        ScriptedToolExecutor Executor)> RunDocumentFocusTransitionAsync(
        string documentFocusEvidenceId)
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "pompe HX-42 exigences",
            topK = 4
        });
        var intake = Intake("Résume les exigences documentées de la pompe HX-42.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "quatre exigences documentées",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "exigences documentées",
                    initialCapability = "rag_search",
                    rowHeader = "Groupe",
                    rowLabels = new[] { "A", "B" },
                    columns = new[] { "Exigence 1", "Exigence 2" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "audit-search",
                new[] { "E1" },
                Array.Empty<string>())),
            Completion(Call(
                "focused-navigation",
                "refine_document_navigation",
                new
                {
                    documentFocusEvidenceId,
                    query = "",
                    navigationKind = "all",
                    limit = 10
                })));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            NavigationResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 3,
                SeparateActionAndWriter = false,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateStrategyEnabled = false
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);
        return (result, llm, executor);
    }

    private static async Task<(
        SourceBackedPipelineResult Result,
        ScriptedAgentLlm Llm,
        ScriptedToolExecutor Executor)> RunDocumentContextExpansionTransitionAsync(
        string documentFocusEvidenceId)
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "pompe HX-42 exigences",
            topK = 4
        });
        var intake = Intake("Résume les exigences documentées de la pompe HX-42.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "quatre exigences documentées",
                    structuredLayout = true,
                    rowCount = 2,
                    columnCount = 2,
                    atomicEvidenceCount = 4,
                    atomicEvidenceType = "exigences documentées",
                    initialCapability = "rag_search",
                    rowHeader = "Groupe",
                    rowLabels = new[] { "A", "B" },
                    columns = new[] { "Exigence 1", "Exigence 2" }
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "audit-search",
                new[] { "E1" },
                Array.Empty<string>())),
            Completion(Call(
                "expand-visible-document",
                "expand_document_context",
                new
                {
                    documentFocusEvidenceId
                })));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            DocumentContextResultAtPage(
                42,
                "Exigences complémentaires documentées de la pompe HX-42."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 3,
                SeparateActionAndWriter = false,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateStrategyEnabled = false
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);
        return (result, llm, executor);
    }

    private static SourceBackedIntake FastReviewIntake()
    {
        var routerAction = Call("router-plan-1", "rag_search", new
        {
            query = "dessert chocolat facile",
            categoryPath = "Cuisine"
        });
        return Intake("Je veux un dessert au chocolat facile.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    routerAction.Id,
                    routerAction.Name,
                    routerAction.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    deliverable = "une proposition de dessert sourcee",
                    structuredLayout = false,
                    rowCount = 1,
                    columnCount = 1,
                    atomicEvidenceCount = 1,
                    atomicEvidenceType = "dessert complet et utilisable",
                    initialCapability = "rag_search",
                    rowHeader = "",
                    rowLabels = Array.Empty<string>(),
                    columns = Array.Empty<string>()
                }),
                "llm_router")
        };
    }

    private static SourceBackedAgentToolCall Call(string id, string name, object arguments)
        => new(id, name, JsonSerializer.SerializeToElement(arguments));

    private static SourceBackedAgentToolCall SelectionCall(
        string id,
        IReadOnlyList<string> evidenceIds)
    {
        if (evidenceIds.Count != 20)
            throw new ArgumentException("This test helper expects a 5 x 4 selection.", nameof(evidenceIds));

        var days = new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" };
        return Call(
            id,
            "submit_evidence_selection",
            new
            {
                evidenceIds,
                layout = new
                {
                    rowHeader = "Jour",
                    columns = new[]
                    {
                        "Petit-dejeuner",
                        "Dejeuner",
                        "Collation",
                        "Souper"
                    },
                    rows = days
                }
            });
    }

    private static SourceBackedAgentToolCall CandidateAuditCall(
        string id,
        IReadOnlyList<string> approvedEvidenceIds,
        IReadOnlyList<string> rejectedEvidenceIds)
    {
        var approved = approvedEvidenceIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var labelIndexes = approvedEvidenceIds
            .Concat(rejectedEvidenceIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static evidenceId => evidenceId,
                evidenceId => approved.Contains(evidenceId) ? 1 : 0,
                StringComparer.OrdinalIgnoreCase);
        return CandidateAuditCall(id, labelIndexes);
    }

    private static SourceBackedAgentToolCall CandidateAuditCall(
        string id,
        IReadOnlyDictionary<string, int> labelIndexesByEvidenceId)
    {
        return Call(
            id,
            "submit_candidate_batch_audit",
            new
            {
                labelIndexesByEvidenceId
            });
    }

    private static async Task<object> InvokeCandidateAuditAsync(
        SourceBackedAgentV2Runner runner,
        IReadOnlyList<EvidenceItem> candidates,
        string semanticPlan = "LIVRABLE: concrete documented options",
        string candidateObjectType = "concrete documented option",
        string candidateEligibilityRule =
            "Le libelle exact nomme une instance autonome du type cible.")
    {
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "CompleteBatchedCandidateAuditAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            runner,
            new object?[]
            {
                Intake("Give me concrete documented options."),
                semanticPlan,
                candidateObjectType,
                candidateEligibilityRule,
                "",
                Array.Empty<string>(),
                Array.Empty<string>(),
                candidates,
                CancellationToken.None
            }));
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static async Task<object> InvokeStructuredFlatWriterAsync(
        SourceBackedAgentV2Runner runner,
        EvidenceBundle bundle,
        IReadOnlyList<string> selectedEvidenceIds,
        string atomicEvidenceMode,
        int? requiredClaimCount = null)
    {
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "CompleteStructuredFlatWriterAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            new object?[]
            {
                Intake("Summarize the documented safety requirements."),
                bundle,
                selectedEvidenceIds,
                 atomicEvidenceMode,
                 null,
                 CancellationToken.None,
                 null,
                 null,
                 requiredClaimCount
             }));
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static T ReadPrivateProperty<T>(object instance, string propertyName)
        => (T)instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(instance)!;

    private static SourceBackedAgentCompletion CandidateStrategy(
        IReadOnlyList<int>? candidateScopeIds = null,
        string candidateObjectType = "instances documentees",
        string candidateEligibilityRule =
            "Le libelle exact nomme une instance autonome du type cible.",
        string candidatePoolRelation = "shared_pool",
        string sourceDiscoveryQuery = "",
        string initialCapability = "documents_content_cards",
        int initialLimit = 4)
        => Completion(Call(
            "candidate-strategy",
            "submit_candidate_evidence_strategy",
            new
            {
                candidateObjectType,
                candidateEligibilityRule,
                initialCapability,
                sourceDiscoveryQuery,
                initialLimit,
                candidateScopeIds = candidateScopeIds ?? new[] { 0 },
                candidatePoolRelation
            }));

    private static SourceBackedAgentCompletion ColumnSemantics(
        params (string Label, string Role)[] columns)
        => Completion(Call(
            "column-semantics",
            "submit_column_semantics",
            new
            {
                columns = columns
                    .Select(static column => new
                    {
                        label = column.Label,
                        inclusion = column.Role,
                        exclusion = "Valeurs reservees aux autres colonnes du livrable."
                    })
                    .ToArray()
            }));

    private static SourceBackedAgentCompletion Completion(SourceBackedAgentToolCall call)
        => new(string.Empty, new[] { call }, "tool_calls", 100, 20);

    private static SourceBackedAgentCompletion Completion(string answer)
        => new(answer, Array.Empty<SourceBackedAgentToolCall>(), "stop", 200, 80);

    private static SourceBackedAgentCompletion FastReview(
        string action,
        string evidenceId,
        string anchorId,
        string text = "")
        => Completion(JsonSerializer.Serialize(new
        {
            action,
            evidenceId,
            anchorId,
            text
        }));

    private static SourceBackedAgentCompletion SingleSelectionScopeReview(
        string decision,
        string selectedEvidenceId,
        string question,
        params (string EvidenceId, string Match)[] candidateMatches)
        => SingleSelectionScopeReviewWithReason(
            decision,
            selectedEvidenceId,
            question,
            "La decision respecte la portee demandee et les candidats visibles.",
            candidateMatches);

    private static SourceBackedAgentCompletion SingleSelectionScopeReviewWithReason(
        string decision,
        string selectedEvidenceId,
        string question,
        string reason,
        params (string EvidenceId, string Match)[] candidateMatches)
        => Completion(JsonSerializer.Serialize(new
        {
            decision,
            candidateMatches = candidateMatches.ToDictionary(
                static candidate => candidate.EvidenceId,
                static candidate => candidate.Match),
            selectedEvidenceId,
            question,
            reason
        }));

    private static SourceBackedIntake SingleSelectionScopeIntake(
        string question,
        string atomicEvidenceType)
    {
        var routerAction = Call("scope-router-1", "rag_search", new
        {
            query = question,
            categoryPath = "Knowledge"
        });
        return Intake(question) with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    routerAction.Id,
                    routerAction.Name,
                    routerAction.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    deliverable = "un item source repondant a la demande",
                    structuredLayout = false,
                    rowCount = 1,
                    columnCount = 1,
                    atomicEvidenceCount = 1,
                    atomicEvidenceType,
                    initialCapability = "rag_search",
                    rowHeader = "",
                    rowLabels = Array.Empty<string>(),
                    columns = Array.Empty<string>()
                }),
                "llm_router")
        };
    }

    private static SourceBackedAgentCompletion SemanticReview(
        string decision,
        string reason,
        IReadOnlyList<string>? rejectedEvidenceIds = null,
        IReadOnlyList<string>? preferredAlternativeEvidenceIds = null)
        => Completion(Call(
            "semantic-review",
            "submit_semantic_review",
            new
            {
                decision,
                reasons = new[] { reason },
                rejectedEvidenceIds = rejectedEvidenceIds ?? Array.Empty<string>(),
                preferredAlternativeEvidenceIds =
                    preferredAlternativeEvidenceIds ?? Array.Empty<string>()
            }));

    private static SourceBackedAgentCompletion StructuredBatchReview(
        params string[] claimRefs)
        => Completion(string.Join(
            Environment.NewLine,
            claimRefs.Select(static claimRef => claimRef + "=ACCEPT")));

    private static ToolResults NavigationResult()
        => Results(
            "documents.navigation",
            new
            {
                items = new[]
                {
                    new
                    {
                        label = "Couple de serrage du couvercle HX-42",
                        docId = "doc-hx42",
                        docName = "HydraulicPumpManual.pdf",
                        docPath = "Manuals/HydraulicPumpManual.pdf",
                        targetPageStart = 42,
                        targetPageEnd = 42
                    }
                }
            });

    private static ToolResults PageOnlyNavigationResult()
        => Results(
            "documents.navigation",
            new
            {
                navigationOnly = true,
                total = 1,
                limit = 1,
                offset = 0,
                items = new[]
                {
                    new
                    {
                        label = "Couple de serrage du couvercle HX-42",
                        kind = "navigation_entry",
                        docId = "doc-hx42",
                        docName = "HydraulicPumpManual.pdf",
                        docPath = "Manuals/HydraulicPumpManual.pdf",
                        categoryPath = "Manuals",
                        revisionId = "revision-hx42",
                        targetAnchorId = "anchor-hx42-torque",
                        targetPageStart = 42,
                        targetPageEnd = 42,
                        hasTargetChunk = false,
                        hasTargetAnchor = true
                    }
                }
            });

    private static ToolResults PageOnlyNavigationContextResult()
        => Results(
            "documents.context",
            new
            {
                document = new
                {
                    docId = "doc-hx42",
                    docName = "HydraulicPumpManual.pdf",
                    docPath = "Manuals/HydraulicPumpManual.pdf"
                },
                items = new[]
                {
                    new
                    {
                        docId = "doc-hx42",
                        docName = "HydraulicPumpManual.pdf",
                        docPath = "Manuals/HydraulicPumpManual.pdf",
                        categoryPath = "Manuals",
                        pageStart = 42,
                        pageEnd = 42,
                        chunkId = "doc-hx42:42:canonical",
                        text = "Pour la pompe HX-42, serrer le couvercle à 85 N·m."
                    }
                }
            });

    private static ToolResults ResolvableNavigationResult()
        => Results(
            "documents.navigation",
            new
            {
                navigationOnly = true,
                total = 2,
                limit = 2,
                offset = 0,
                items = new[]
                {
                    new
                    {
                        label = "Option Alpha",
                        kind = "navigation_entry",
                        docId = "doc-options",
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf",
                        categoryPath = "Knowledge",
                        revisionId = "revision-options",
                        targetChunkId = "chunk-alpha",
                        targetAnchorId = "anchor-alpha",
                        targetPageStart = 11,
                        targetPageEnd = 11
                    },
                    new
                    {
                        label = "Option Beta",
                        kind = "navigation_entry",
                        docId = "doc-options",
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf",
                        categoryPath = "Knowledge",
                        revisionId = "revision-options",
                        targetChunkId = "chunk-beta",
                        targetAnchorId = "anchor-beta",
                        targetPageStart = 22,
                        targetPageEnd = 22
                    }
                }
            });

    private static ToolResults NavigationBatchContextResults()
    {
        var results = new ToolResults();
        foreach (var item in new[]
                 {
                     (ChunkId: "chunk-alpha", Page: 11, Text: "Option Alpha avec contenu source complet."),
                     (ChunkId: "chunk-beta", Page: 22, Text: "Option Beta avec contenu source complet.")
                 })
        {
            results.Items.Add(new ToolResults.Item
            {
                ToolName = "documents.context",
                DurationMs = 12,
                Result = JsonSerializer.SerializeToElement(new
                {
                    document = new
                    {
                        docId = "doc-options",
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf"
                    },
                    items = new[]
                    {
                        new
                        {
                            docId = "doc-options",
                            docName = "Options.pdf",
                            docPath = "Knowledge/Options.pdf",
                            categoryPath = "Knowledge",
                            pageStart = item.Page,
                            pageEnd = item.Page,
                            chunkId = item.ChunkId,
                            text = item.Text
                        }
                    }
                })
            });
        }
        return results;
    }

    private static ToolResults SearchResult(string toolName = "rag.search")
        => Results(
            toolName,
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "doc-hx42",
                        docName = "HydraulicPumpManual.pdf",
                        docPath = "Manuals/HydraulicPumpManual.pdf",
                        pageStart = 42,
                        pageEnd = 42,
                        chunkId = "doc-hx42:42:3",
                        excerpt = "Pour la pompe HX-42, serrer les boulons du couvercle en croix à 85 N·m.",
                        score = 0.97
                    }
                }
            });

    private static ToolResults DocumentIdentityOnlySearchResult()
        => Results(
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "ansi-b11",
                        docName = "ANSI B11.0-2023 - Safety of Machinery.pdf",
                        docPath = "Normes/PDF/ANSI B11.0-2023 - Safety of Machinery.pdf",
                        pageStart = 8,
                        pageEnd = 8,
                        chunkId = "ansi-b11:title",
                        excerpt = "B11.0-2023",
                        score = 0.97
                    }
                }
            });

    private static ToolResults DocumentIdentityAndContentSearchResult()
        => Results(
            "rag.search",
            new
            {
                hits = new object[]
                {
                    new
                    {
                        docId = "ansi-b11",
                        docName = "ANSI B11.0-2023 - Safety of Machinery.pdf",
                        docPath = "Normes/PDF/ANSI B11.0-2023 - Safety of Machinery.pdf",
                        pageStart = 8,
                        pageEnd = 8,
                        chunkId = "ansi-b11:title",
                        excerpt = "B11.0-2023",
                        score = 0.97
                    },
                    new
                    {
                        docId = "ansi-b11",
                        docName = "ANSI B11.0-2023 - Safety of Machinery.pdf",
                        docPath = "Normes/PDF/ANSI B11.0-2023 - Safety of Machinery.pdf",
                        pageStart = 42,
                        pageEnd = 42,
                        chunkId = "ansi-b11:requirement",
                        excerpt = "The guard shall prevent access to the hazard zone.",
                        score = 0.94
                    },
                    new
                    {
                        docId = "ansi-b11",
                        docName = "ANSI B11.0-2023 - Safety of Machinery.pdf",
                        docPath = "Normes/PDF/ANSI B11.0-2023 - Safety of Machinery.pdf",
                        pageStart = 42,
                        pageEnd = 42,
                        chunkId = "ansi-b11:stop",
                        excerpt = "The stop control shall be readily accessible to the operator.",
                        score = 0.92
                    }
                }
            });

    private static ToolResults SevenContentClaimSearchResult(int startIndex = 1)
        => Results(
            "rag.search",
            new
            {
                hits = Enumerable.Range(startIndex, 7)
                    .Select(static index => new
                    {
                        docId = "safety-standard",
                        docName = "Safety Standard.pdf",
                        docPath = "Standards/Safety Standard.pdf",
                        pageStart = 20 + index,
                        pageEnd = 20 + index,
                        chunkId = $"safety-standard:requirement:{index}",
                        excerpt =
                            $"Documented operational safety requirement {index}.",
                        score = 1d - index / 100d
                    })
                    .ToArray()
            });

    private static ToolResults SingleEvidenceWithTwoClaimsSearchResult()
        => Results(
            "rag.search",
            new
            {
                hits = new object[]
                {
                    new
                    {
                        docId = "safety-standard",
                        docName = "Safety Standard.pdf",
                        docPath = "Standards/Safety Standard.pdf",
                        pageStart = 42,
                        pageEnd = 42,
                        chunkId = "safety-standard:two-requirements",
                        excerpt =
                            "The guard shall prevent access to the hazard zone. "
                            + "The stop control shall be readily accessible to the operator.",
                        score = 0.96
                    }
                }
            });

    private static ToolResults SearchResultWithAnEmptyEvidenceItem()
        => Results(
            "rag.search",
            new
            {
                hits = new object[]
                {
                    new
                    {
                        docId = "doc-a",
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf",
                        pageStart = 1,
                        pageEnd = 1,
                        chunkId = "option-a",
                        excerpt = "Documented option A.",
                        score = 0.93
                    },
                    new
                    {
                        docId = "doc-empty",
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf",
                        pageStart = 2,
                        pageEnd = 2,
                        chunkId = "option-empty",
                        excerpt = "---",
                        score = 0.92
                    },
                    new
                    {
                        docId = "doc-b",
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf",
                        pageStart = 3,
                        pageEnd = 3,
                        chunkId = "option-b",
                        excerpt = "Documented option B.",
                        score = 0.91
                    }
                }
            });

    private static ToolResults SearchResultAtPage(
        int page,
        string excerpt)
        => Results(
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "doc-" + page,
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf",
                        pageStart = page,
                        pageEnd = page,
                        chunkId = "option-" + page,
                        excerpt,
                        score = 0.9
                    }
                }
            });

    private static ToolResults FastReviewDistinctBatchResult(
        int firstEvidenceNumber)
        => Results(
            "rag.search",
            new
            {
                hits = Enumerable.Range(firstEvidenceNumber, 3)
                    .Select(index => new
                    {
                        docId = "doc-fast-review-" + index,
                        docName = $"FastReview-{index}.pdf",
                        docPath = $"Knowledge/FastReview-{index}.pdf",
                        pageStart = index,
                        pageEnd = index,
                        chunkId = "fast-review-" + index,
                        excerpt = $"Candidat documente distinct numero {index}.",
                        score = 1.0 - (index * 0.01)
                    })
                    .ToArray()
            });

    private static ToolResults FastReviewWideBatchResult(
        int firstEvidenceNumber)
        => Results(
            "rag.search",
            new
            {
                hits = Enumerable.Range(firstEvidenceNumber, 5)
                    .Select(index => new
                    {
                        docId = "doc-wide-review-" + index,
                        docName = $"WideReview-{index}.pdf",
                        docPath = $"Knowledge/WideReview-{index}.pdf",
                        pageStart = index,
                        pageEnd = index,
                        chunkId = "wide-review-" + index,
                        excerpt = $"Preuve documentaire generique numero {index}.",
                        score = 1.0 - (index * 0.01)
                    })
                    .ToArray()
            });

    private static ToolResults CompactDiversitySearchResult()
        => Results(
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "doc-options",
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf",
                        pageStart = 2,
                        pageEnd = 2,
                        chunkId = "option-b",
                        excerpt = "Option issue de la source visible B.",
                        score = 0.90
                    },
                    new
                    {
                        docId = "doc-options",
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf",
                        pageStart = 3,
                        pageEnd = 3,
                        chunkId = "option-c",
                        excerpt = "Option issue de la source visible C.",
                        score = 0.89
                    },
                    new
                    {
                        docId = "doc-options",
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf",
                        pageStart = 1,
                        pageEnd = 1,
                        chunkId = "option-a-1",
                        excerpt = "Premiere alternative de la source visible A.",
                        score = 0.88
                    },
                    new
                    {
                        docId = "doc-options",
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf",
                        pageStart = 1,
                        pageEnd = 1,
                        chunkId = "option-a-2",
                        excerpt = "Deuxieme alternative de la source visible A.",
                        score = 0.87
                    },
                    new
                    {
                        docId = "doc-options",
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf",
                        pageStart = 1,
                        pageEnd = 1,
                        chunkId = "option-a-3",
                        excerpt = "Troisieme alternative de la source visible A.",
                        score = 0.86
                    }
                }
            });

    private static ToolResults SamePageSourceWindowResult()
        => Results(
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "doc-data-sheet",
                        docName = "sample.pdf",
                        docPath = "Knowledge/Data sheets/sample.pdf",
                        pageStart = 1,
                        pageEnd = 1,
                        chunkId = "features-1",
                        excerpt =
                            "Features and Benefits. Dense polymer structure.",
                        score = 0.99
                    },
                    new
                    {
                        docId = "doc-data-sheet",
                        docName = "sample.pdf",
                        docPath = "Knowledge/Data sheets/sample.pdf",
                        pageStart = 1,
                        pageEnd = 1,
                        chunkId = "properties-2",
                        excerpt =
                            "Typical Properties Powder Test Method Bulk Density.",
                        score = 0.98
                    }
                }
            });

    private static ToolResults DocumentContextResultAtPage(
        int page,
        string text)
        => Results(
            "documents.context",
            new
            {
                document = new
                {
                    docId = "doc-" + page,
                    docName = "Options.pdf",
                    docPath = "Knowledge/Options.pdf"
                },
                items = new[]
                {
                    new
                    {
                        docId = "doc-" + page,
                        docName = "Options.pdf",
                        docPath = "Knowledge/Options.pdf",
                        pageStart = page,
                        pageEnd = page,
                        chunkId = "option-" + page,
                        text
                    }
                }
            });

    private static ToolResults FastReviewDuplicateSourceResult()
        => Results(
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "doc-eclairs",
                        docName = "recettes.pdf",
                        docPath = "Cuisine/recettes.pdf",
                        pageStart = 34,
                        pageEnd = 34,
                        chunkId = "eclairs-1",
                        excerpt =
                            "Eclairs au chocolat. Preparation 40 minutes. Niveau intermediaire.",
                        score = 0.96
                    },
                    new
                    {
                        docId = "doc-eclairs",
                        docName = "recettes.pdf",
                        docPath = "Cuisine/recettes.pdf",
                        pageStart = 34,
                        pageEnd = 34,
                        chunkId = "eclairs-2",
                        excerpt =
                            "Eclairs au chocolat. Creme patissiere et poche a douille.",
                        score = 0.94
                    },
                    new
                    {
                        docId = "doc-truffes",
                        docName = "robot.pdf",
                        docPath = "Cuisine/robot.pdf",
                        pageStart = 155,
                        pageEnd = 155,
                        chunkId = "truffes-1",
                        excerpt =
                            "TRUFFES FACILES. Chocolat et beurre, puis boules roulees dans le cacao.",
                        score = 0.73
                    }
                }
            });

    private static ToolResults SingleSelectionTechnicalProceduresResult()
        => Results(
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "doc-procedure-atlas",
                        docName = "procedure-atlas.pdf",
                        docPath = "Knowledge/procedure-atlas.pdf",
                        pageStart = 4,
                        pageEnd = 4,
                        chunkId = "procedure-atlas-4",
                        excerpt =
                            "Relance du capteur: coupez l'alimentation pendant 10 secondes puis relancez le capteur.",
                        score = 0.94
                    },
                    new
                    {
                        docId = "doc-procedure-boreal",
                        docName = "procedure-boreal.pdf",
                        docPath = "Knowledge/procedure-boreal.pdf",
                        pageStart = 9,
                        pageEnd = 9,
                        chunkId = "procedure-boreal-9",
                        excerpt =
                            "Relance alternative: maintenez les deux commandes pendant 5 secondes.",
                        score = 0.91
                    }
                }
            });

    private static ToolResults SingleSelectionComplianceLabelsResult()
        => Results(
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "doc-label-north",
                        docName = "label-north.pdf",
                        docPath = "Knowledge/label-north.pdf",
                        pageStart = 12,
                        pageEnd = 12,
                        chunkId = "label-north-12",
                        excerpt =
                            "Pour une utilisation en Region Nord, appliquez l'etiquette N-42.",
                        score = 0.95
                    },
                    new
                    {
                        docId = "doc-label-south",
                        docName = "label-south.pdf",
                        docPath = "Knowledge/label-south.pdf",
                        pageStart = 7,
                        pageEnd = 7,
                        chunkId = "label-south-7",
                        excerpt =
                            "Pour une utilisation en Region Sud, appliquez l'etiquette S-17.",
                        score = 0.93
                    }
                }
            });

    private static ToolResults FastReviewSingleSourceAlternativesResult(
        int count = 3)
        => Results(
            "rag.search",
            new
            {
                hits = Enumerable.Range(1, count)
                    .Select(index => new
                    {
                        docId = "doc-options",
                        docName = "options.pdf",
                        docPath = "Knowledge/options.pdf",
                        pageStart = 1,
                        pageEnd = 1,
                        chunkId = "option-" + index,
                        excerpt =
                            $"Option documentee {index} avec contenu concret et utilisable.",
                        score = 0.97 - (index * 0.01)
                    })
                    .ToArray()
            });

    private static ToolResults ContextResultWithVisibleSourceDuplicate(
        int firstIndex,
        int count)
        => Results(
            "documents.context",
            new
            {
                document = new
                {
                    docId = "doc-options",
                    docName = "options.pdf",
                    docPath = "Knowledge/options.pdf"
                },
                items = Enumerable.Range(firstIndex, count)
                    .Select(index => new
                    {
                        docId = "doc-options",
                        docName = "options.pdf",
                        docPath = "Knowledge/options.pdf",
                        categoryPath = "Knowledge",
                        pageStart = index <= 2 ? 1 : index - 1,
                        pageEnd = index <= 2 ? 1 : index - 1,
                        chunkId = "option-" + index,
                        text = $"Option documentee {index} avec contenu concret."
                    })
                    .ToArray()
            });

    private static ToolResults SearchResultWithCards(
        string docPath,
        string categoryPath,
        string prefix,
        int page,
        int cardCount)
        => Results(
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "doc-" + prefix,
                        docName = Path.GetFileName(docPath),
                        docPath,
                        categoryPath,
                        pageStart = page,
                        pageEnd = page,
                        chunkId = "chunk-" + prefix,
                        excerpt = "Page de recettes " + prefix,
                        score = 0.95,
                        matchedContentCards = Enumerable.Range(1, cardCount)
                            .Select(index => new
                            {
                                contentCardId = $"{prefix}-card-{index}",
                                title = $"{prefix}-card-{index}",
                                pageStart = page,
                                pageEnd = page,
                                evidence = new { sourceText = $"Preuve {prefix} {index}" }
                            })
                            .ToArray()
                    }
                }
            });

    private static ToolResults ContentCardInventoryResult()
        => Results(
            "documents.content_cards",
            new
            {
                citable = true,
                query = "recette",
                total = 1,
                limit = 60,
                offset = 0,
                items = new[]
                {
                    new
                    {
                        docId = "doc-recettes",
                        docName = "chefbot_livre_de_recettes_fr.pdf",
                        docPath = "Cuisine/chefbot_livre_de_recettes_fr.pdf",
                        categoryPath = "Cuisine",
                        revisionId = "revision-recettes",
                        sourceHash = "hash-recettes",
                        contentCardId = "card-feta",
                        profileVersion = "foundation_v1",
                        cardIndex = 12,
                        title = "Sticks de feta",
                        kind = "page_embedded_title",
                        pageStart = 21,
                        pageEnd = 21,
                        hasGroundedEvidence = true,
                        queryScore = 0.84,
                        evidence = new
                        {
                            facts = new[]
                            {
                                new
                                {
                                    sourceText = "20 pieces",
                                    pageStart = 21,
                                    pageEnd = 21
                                }
                            }
                        }
                    }
                }
            });

    private static ToolResults PagedContentCardInventoryResult()
        => Results(
            "documents.content_cards",
            new
            {
                citable = true,
                query = "",
                total = 12,
                limit = 5,
                offset = 0,
                nextOffset = 5,
                items = new[]
                {
                    new
                    {
                        docId = "doc-recettes",
                        docName = "chefbot_livre_de_recettes_fr.pdf",
                        docPath = "Cuisine/chefbot_livre_de_recettes_fr.pdf",
                        categoryPath = "Cuisine",
                        revisionId = "revision-recettes",
                        sourceHash = "hash-recettes",
                        contentCardId = "card-feta",
                        profileVersion = "foundation_v1",
                        cardIndex = 12,
                        title = "Sticks de feta",
                        kind = "page_embedded_title",
                        pageStart = 21,
                        pageEnd = 21,
                        hasGroundedEvidence = true,
                        evidence = new
                        {
                            sourceText = "Preuve documentaire de la carte paginee."
                        }
                    }
                }
            });

    private static IReadOnlyList<MealCard> TwentyMealCards()
    {
        var slots = new[]
        {
            "Lundi-Petit-dejeuner", "Lundi-Dejeuner", "Lundi-Collation", "Lundi-Souper",
            "Mardi-Petit-dejeuner", "Mardi-Dejeuner", "Mardi-Collation", "Mardi-Souper",
            "Mercredi-Petit-dejeuner", "Mercredi-Dejeuner", "Mercredi-Collation", "Mercredi-Souper",
            "Jeudi-Petit-dejeuner", "Jeudi-Dejeuner", "Jeudi-Collation", "Jeudi-Souper",
            "Vendredi-Petit-dejeuner", "Vendredi-Dejeuner", "Vendredi-Collation", "Vendredi-Souper"
        };
        return slots
            .Select((slot, index) => new MealCard(
                "Recette " + (index + 1) + " " + slot + " "
                + new string((char)('A' + index), 115),
                index + 1))
            .ToArray();
    }

    private static ToolResults ContentCardInventoryResult(IReadOnlyList<MealCard> cards)
        => Results(
            "documents.content_cards",
            new
            {
                citable = true,
                total = cards.Count,
                limit = 40,
                offset = 0,
                items = cards.Select((card, index) => new
                {
                    docId = "doc-recettes",
                    docName = "recettes.pdf",
                    docPath = "Cuisine/recettes.pdf",
                    categoryPath = "Cuisine",
                    revisionId = "revision-recettes",
                    sourceHash = "hash-recettes",
                    contentCardId = "card-" + (index + 1),
                    profileVersion = "foundation_v1",
                    cardIndex = index,
                    title = card.Title,
                    kind = "page_embedded_title",
                    headingPath = card.HeadingPath ?? card.Title,
                    pageStart = card.Page,
                    pageEnd = card.Page,
                    hasGroundedEvidence = true,
                    evidence = new
                    {
                        sourceText = "Preuve documentaire pour " + card.Title
                    }
                }).ToArray()
            });

    private sealed record MealCard(
        string Title,
        int Page,
        string? HeadingPath = null);

    private static ToolResults Results(string toolName, object payload)
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = toolName,
            Result = BuildCanonicalTestPayload(payload),
            DurationMs = 12
        });
        return results;
    }

    private static JsonElement BuildCanonicalTestPayload(object payload)
    {
        var root = JsonSerializer.SerializeToNode(payload)
                   ?? throw new InvalidOperationException("Test payload could not be serialized.");
        AddCanonicalTestIdentityDefaults(root);
        return JsonSerializer.SerializeToElement(root);
    }

    private static void AddCanonicalTestIdentityDefaults(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var hasDocumentIdentity = obj.Any(static pair =>
                string.Equals(pair.Key, "docId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "docPath", StringComparison.OrdinalIgnoreCase));
            if (hasDocumentIdentity)
            {
                if (!obj.TryGetPropertyValue("revisionId", out var revisionNode)
                    || revisionNode is null
                    || string.IsNullOrWhiteSpace(revisionNode.GetValue<string>()))
                {
                    obj["revisionId"] = "revision-test-active";
                }

                if (!obj.TryGetPropertyValue("sourceHash", out var hashNode)
                    || hashNode is null
                    || !IsSha256HexForTest(hashNode.GetValue<string>()))
                {
                    obj["sourceHash"] = new string('a', 64);
                }
            }

            foreach (var child in obj.ToArray())
            {
                if (child.Value is not null)
                    AddCanonicalTestIdentityDefaults(child.Value);
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                    AddCanonicalTestIdentityDefaults(child);
            }
        }
    }

    private static bool IsSha256HexForTest(string? value)
        => value is { Length: 64 }
           && value.All(static character =>
               character is >= '0' and <= '9'
               or >= 'a' and <= 'f'
               or >= 'A' and <= 'F');

    private sealed class ScriptedAgentLlm :
        ISourceBackedAgentLlmClient,
        ISourceBackedAgentStructuredLlmClient
    {
        private readonly Queue<SourceBackedAgentCompletion> _completions;
        private ScriptedCandidateAuditPlan? _candidateAuditPlan;

        public ScriptedAgentLlm(params SourceBackedAgentCompletion[] completions)
            => _completions = new Queue<SourceBackedAgentCompletion>(completions);

        public List<IReadOnlyList<SourceBackedAgentMessage>> Requests { get; } = new();
        public List<IReadOnlyList<SourceBackedAgentToolDefinition>> ToolSets { get; } = new();
        public List<double?> Temperatures { get; } = new();
        public List<int> MaxTokens { get; } = new();
        public List<bool> RequireToolCalls { get; } = new();
        public Func<string, IReadOnlyList<string>, IReadOnlyList<string>>?
            CandidateColumnCompatibilitySelector
        { get; init; }
        public List<LlmStructuredOutputContract> StructuredOutputContracts
        {
            get;
        } = new();

        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null)
        {
            StructuredOutputContracts.Add(contract);
            return CompleteAsync(
                messages,
                Array.Empty<SourceBackedAgentToolDefinition>(),
                maxTokens,
                ct,
                temperatureOverride,
                requireToolCall: false);
        }

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            Requests.Add(messages.ToArray());
            ToolSets.Add(tools.ToArray());
            Temperatures.Add(temperatureOverride);
            MaxTokens.Add(maxTokens);
            RequireToolCalls.Add(requireToolCall);
            if (TryCompleteScriptedNavigationAnchorAudit(
                    messages,
                    _completions.Count > 0 ? _completions.Peek() : null,
                    out var navigationAnchorCompletion))
            {
                return Task.FromResult(navigationAnchorCompletion);
            }
            if (TryCompleteScriptedCandidateColumnCompatibility(
                    messages,
                    CandidateColumnCompatibilitySelector,
                    out var compatibilityCompletion))
            {
                return Task.FromResult(compatibilityCompletion);
            }
            if (TryCompleteScriptedCandidateAudit(
                    messages,
                    ref _candidateAuditPlan,
                    _completions.Count > 0 ? _completions.Peek() : null,
                    out var auditCompletion,
                    out var consumePlanCompletion))
            {
                if (consumePlanCompletion)
                    _completions.Dequeue();
                return Task.FromResult(auditCompletion);
            }
            if (_completions.Count == 0)
            {
                throw new InvalidOperationException(
                    "Scripted completion queue empty. Tool history="
                    + string.Join(
                        " => ",
                        ToolSets.Select(static set =>
                            "[" + string.Join(",", set.Select(static tool => tool.Name)) + "]"))
                    + "; current tools="
                    + string.Join(",", tools.Select(static tool => tool.Name))
                    + "; prompt="
                    + string.Join(
                        " | ",
                        messages.Select(static message => message.Content))
                        .Replace(Environment.NewLine, " ")
                        .Substring(
                            0,
                            Math.Min(
                                600,
                                string.Join(
                                    " | ",
                                    messages.Select(static message => message.Content))
                                    .Replace(Environment.NewLine, " ")
                                    .Length)));
            }
            return Task.FromResult(_completions.Dequeue());
        }
    }

    private sealed class ToolAwareContentClaimGapLlm :
        ISourceBackedAgentLlmClient
    {
        private int _focusedSearchCount;
        private int _flatEvidenceAdequacyCount;

        public List<IReadOnlyList<SourceBackedAgentMessage>> Requests { get; } = new();
        public List<IReadOnlyList<SourceBackedAgentToolDefinition>> ToolSets { get; } = new();

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            Requests.Add(messages.ToArray());
            ToolSets.Add(tools.ToArray());
            if (tools.Any(static tool =>
                    tool.Name == "submit_flat_evidence_gap"))
            {
                _flatEvidenceAdequacyCount++;
                if (_flatEvidenceAdequacyCount == 2)
                {
                    return Task.FromResult(new SourceBackedAgentCompletion(
                        string.Empty,
                        Array.Empty<SourceBackedAgentToolCall>(),
                        "length",
                        200,
                        320));
                }
                return Task.FromResult(Completion(Call(
                    "flat-gap",
                    "submit_flat_evidence_gap",
                    new
                    {
                        usefulEvidenceIds = new[] { "E1", "E2" },
                        missingRequirements = new[]
                        {
                            "Five additional operational requirements"
                        },
                        reason =
                            "Two requirements are useful but five requested points remain unsupported."
                    })));
            }
            if (tools.Any(static tool =>
                    tool.Name == "resolve_source_yield"))
            {
                return Task.FromResult(Completion(Call(
                    "resolve-gap",
                    "resolve_source_yield",
                    new
                    {
                        decision = "insufficiency",
                        message =
                            "Five requested operational requirements remain unsupported by the observed extracts."
                    })));
            }
            if (tools.Any(static tool =>
                    tool.Name == "declare_source_insufficiency"))
            {
                return Task.FromResult(Completion(Call(
                    "last-distinct-route",
                    "submit_research_action",
                    new
                    {
                        capability = "rag_search",
                        query = "remaining machine operation requirements",
                        scope = "",
                        document = "Safety Standard.pdf",
                        anchor = "",
                        navigationKind = "",
                        limit = 7,
                        offset = 0,
                        mode = "focused"
                    })));
            }
            if (tools.Any(static tool =>
                    tool.Name == "refine_focused_document_search"))
            {
                _focusedSearchCount++;
                return Task.FromResult(Completion(Call(
                    $"focused-search-{_focusedSearchCount}",
                    "refine_focused_document_search",
                    new
                    {
                        documentFocusEvidenceId = "E1",
                        query = "missing operational safety requirements",
                        limit = 7
                    })));
            }

            throw new InvalidOperationException(
                "Unexpected tool set while preserving a semantic evidence gap: "
                + string.Join(",", tools.Select(static tool => tool.Name)));
        }
    }

    private sealed class ConcurrentCandidateAuditLlm : ISourceBackedAgentLlmClient
    {
        private int _activeCalls;
        private int _callCount;
        private int _maximumConcurrency;

        public int CallCount => Volatile.Read(ref _callCount);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _activeCalls);
            while (true)
            {
                var observed = Volatile.Read(ref _maximumConcurrency);
                if (active <= observed
                    || Interlocked.CompareExchange(
                        ref _maximumConcurrency,
                        active,
                        observed) == observed)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(10, ct);
                Assert.Empty(tools);
                Assert.False(requireToolCall);
                Assert.Equal(0, temperatureOverride);
                return Completion("LABEL=1");
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }
    }

    private sealed class PromptAwareCandidateAuditLlm :
        ISourceBackedAgentLlmClient
    {
        private readonly Func<string, SourceBackedAgentCompletion> _completion;

        public PromptAwareCandidateAuditLlm(
            Func<string, SourceBackedAgentCompletion> completion)
            => _completion = completion;

        public List<IReadOnlyList<SourceBackedAgentMessage>> Requests { get; } = new();
        public List<IReadOnlyList<SourceBackedAgentToolDefinition>> ToolSets { get; } = new();
        public List<double?> Temperatures { get; } = new();
        public List<bool> RequireToolCalls { get; } = new();

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            Requests.Add(messages.ToArray());
            ToolSets.Add(tools.ToArray());
            Temperatures.Add(temperatureOverride);
            RequireToolCalls.Add(requireToolCall);
            return Task.FromResult(_completion(string.Join(
                Environment.NewLine,
                messages.Select(static message => message.Content))));
        }
    }

    private sealed class ContextOverflowOnceLlm : ISourceBackedAgentLlmClient
    {
        private readonly Queue<object> _steps;
        private ScriptedCandidateAuditPlan? _candidateAuditPlan;

        public ContextOverflowOnceLlm(params object[] steps)
            => _steps = new Queue<object>(steps);

        public List<IReadOnlyList<SourceBackedAgentMessage>> Requests { get; } = new();
        public List<IReadOnlyList<SourceBackedAgentToolDefinition>> ToolSets { get; } = new();

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            Requests.Add(messages.ToArray());
            ToolSets.Add(tools.ToArray());
            var nextCompletion = _steps.Count > 0
                ? _steps.Peek() as SourceBackedAgentCompletion
                : null;
            if (TryCompleteScriptedCandidateAudit(
                    messages,
                    ref _candidateAuditPlan,
                    nextCompletion,
                    out var auditCompletion,
                    out var consumePlanCompletion))
            {
                if (consumePlanCompletion)
                    _steps.Dequeue();
                return Task.FromResult(auditCompletion);
            }
            var step = _steps.Dequeue();
            return step is Exception exception
                ? Task.FromException<SourceBackedAgentCompletion>(exception)
                : Task.FromResult((SourceBackedAgentCompletion)step);
        }
    }

    private sealed record ScriptedCandidateAuditPlan(
        IReadOnlyDictionary<string, int> LabelIndexesByEvidenceId,
        HashSet<string> PendingEvidenceIds);

    private static bool TryCompleteScriptedNavigationAnchorAudit(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        SourceBackedAgentCompletion? nextCompletion,
        out SourceBackedAgentCompletion completion)
    {
        completion = default!;
        if (nextCompletion?.ToolCalls.Count == 1
            && string.Equals(
                nextCompletion.ToolCalls[0].Name,
                "submit_candidate_batch_audit",
                StringComparison.Ordinal))
        {
            return false;
        }
        var prompt = string.Join(
            Environment.NewLine,
            messages.Select(static message => message.Content));
        if (!prompt.Contains(
                "SAAIA_SOURCE_BACKED_STEP=NavigationAnchorEligibilityAudit",
                StringComparison.Ordinal))
        {
            return false;
        }
        var candidateCount = System.Text.RegularExpressions.Regex.Matches(
            prompt,
            @"(?m)^c\d+\s+\[E\d{1,4}\]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count;
        completion = Completion(JsonSerializer.Serialize(new
        {
            decisions = Enumerable.Repeat(1, candidateCount).ToArray()
        }));
        return true;
    }

    private static bool TryCompleteScriptedCandidateColumnCompatibility(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>>? selector,
        out SourceBackedAgentCompletion completion)
    {
        completion = default!;
        var prompt = string.Join(
            Environment.NewLine,
            messages.Select(static message => message.Content));
        if (!prompt.Contains(
                "SAAIA_SOURCE_BACKED_STEP=CandidateColumnCompatibility",
                StringComparison.Ordinal))
        {
            return false;
        }

        var evidenceIds = System.Text.RegularExpressions.Regex.Matches(
                prompt,
                @"(?m)^(E\d{1,4})\s*=",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Select(static match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var roleMatch = System.Text.RegularExpressions.Regex.Match(
            prompt,
            @"(?m)^TARGET ROLE LABEL:\s*(.+)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var selectedEvidenceIds = selector is null
            ? evidenceIds
            : selector(
                roleMatch.Success ? roleMatch.Groups[1].Value.Trim() : string.Empty,
                evidenceIds);
        completion = Completion(JsonSerializer.Serialize(new
        {
            compatibleCandidateIds = selectedEvidenceIds
        }));
        return true;
    }

    private static bool TryCompleteScriptedCandidateAudit(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        ref ScriptedCandidateAuditPlan? activePlan,
        SourceBackedAgentCompletion? nextCompletion,
        out SourceBackedAgentCompletion completion,
        out bool consumePlanCompletion)
    {
        completion = default!;
        consumePlanCompletion = false;
        var prompt = string.Join(
            Environment.NewLine,
            messages.Select(static message => message.Content));
        var candidateMatch = System.Text.RegularExpressions.Regex.Match(
            prompt,
            @"CANDIDAT UNIQUE:\s*(E\d{1,4})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!candidateMatch.Success)
            return false;

        if (activePlan is null
            && TryReadScriptedCandidateAuditPlan(nextCompletion, out var plan))
        {
            activePlan = plan;
            consumePlanCompletion = true;
        }
        if (activePlan is null)
            return false;

        var evidenceId = candidateMatch.Groups[1].Value;
        if (!activePlan.LabelIndexesByEvidenceId.TryGetValue(
                evidenceId,
                out var labelIndex)
            || !activePlan.PendingEvidenceIds.Remove(evidenceId))
        {
            throw new InvalidOperationException(
                "No scripted independent candidate decision for " + evidenceId + ".");
        }

        completion = Completion(labelIndex == 0
            ? "REJECT"
            : "LABEL=" + labelIndex);
        if (activePlan.PendingEvidenceIds.Count == 0)
            activePlan = null;
        return true;
    }

    private static bool TryReadScriptedCandidateAuditPlan(
        SourceBackedAgentCompletion? completion,
        out ScriptedCandidateAuditPlan plan)
    {
        plan = default!;
        if (completion is null
            || completion.ToolCalls.Count != 1
            || !string.Equals(
                completion.ToolCalls[0].Name,
                "test_independent_candidate_audit_plan",
                StringComparison.Ordinal))
        {
            return false;
        }
        var arguments = completion.ToolCalls[0].Arguments;
        if (!arguments.TryGetProperty(
                "labelIndexesByEvidenceId",
                out var indexes)
            || indexes.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Scripted candidate audit plan is malformed.");
        }

        var values = indexes.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value.GetInt32(),
            StringComparer.OrdinalIgnoreCase);
        plan = new ScriptedCandidateAuditPlan(
            values,
            values.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        return true;
    }

    private sealed class ExactTokenCountLlm :
        ISourceBackedAgentLlmClient,
        ISourceBackedAgentInputTokenCounter
    {
        private readonly Queue<SourceBackedAgentCompletion> _completions;
        private readonly Queue<int> _inputTokenCounts;

        public ExactTokenCountLlm(
            IEnumerable<int> inputTokenCounts,
            params SourceBackedAgentCompletion[] completions)
        {
            _inputTokenCounts = new Queue<int>(inputTokenCounts);
            _completions = new Queue<SourceBackedAgentCompletion>(completions);
        }

        public List<int> InputTokenCountsReturned { get; } = new();
        public List<IReadOnlyList<SourceBackedAgentMessage>> Requests { get; } = new();
        public int CompleteCallCount { get; private set; }

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            CompleteCallCount++;
            Requests.Add(messages.ToArray());
            return Task.FromResult(_completions.Dequeue());
        }

        public Task<int?> CountInputTokensAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            CancellationToken ct,
            bool requireToolCall = false)
        {
            var count = _inputTokenCounts.Dequeue();
            InputTokenCountsReturned.Add(count);
            return Task.FromResult<int?>(count);
        }
    }

    private sealed class TokenCountingScriptedAgentLlm :
        ISourceBackedAgentLlmClient,
        ISourceBackedAgentInputTokenCounter
    {
        private readonly ScriptedAgentLlm _inner;
        private readonly Queue<int> _inputTokenCounts;

        public TokenCountingScriptedAgentLlm(
            IEnumerable<int> inputTokenCounts,
            params SourceBackedAgentCompletion[] completions)
        {
            _inputTokenCounts = new Queue<int>(inputTokenCounts);
            _inner = new ScriptedAgentLlm(completions);
        }

        public List<int> InputTokenCountsReturned { get; } = new();
        public List<IReadOnlyList<SourceBackedAgentMessage>> CountedRequests { get; } = new();
        public List<IReadOnlyList<SourceBackedAgentToolDefinition>> CountedToolSets { get; } = new();
        public int CompleteCallCount { get; private set; }
        public List<IReadOnlyList<SourceBackedAgentMessage>> Requests
            => _inner.Requests;

        public async Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            CompleteCallCount++;
            return await _inner.CompleteAsync(
                messages,
                tools,
                maxTokens,
                ct,
                temperatureOverride,
                requireToolCall);
        }

        public Task<int?> CountInputTokensAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            CancellationToken ct,
            bool requireToolCall = false)
        {
            var count = _inputTokenCounts.Dequeue();
            InputTokenCountsReturned.Add(count);
            CountedRequests.Add(messages.ToArray());
            CountedToolSets.Add(tools.ToArray());
            return Task.FromResult<int?>(count);
        }
    }

    private sealed class ScriptedToolExecutor : ISourceBackedAgentToolExecutor
    {
        private readonly Queue<ToolResults> _results;

        public ScriptedToolExecutor(params ToolResults[] results)
            => _results = new Queue<ToolResults>(results);

        public List<string> ToolNames { get; } = new();
        public List<JsonElement> Arguments { get; } = new();

        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
        {
            ToolNames.Add(toolName);
            Arguments.Add(arguments.Clone());
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class ConcurrentToolExecutor : ISourceBackedAgentToolExecutor
    {
        private int _active;
        private int _maximumConcurrency;

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var observed = Volatile.Read(ref _maximumConcurrency);
                if (observed >= active
                    || Interlocked.CompareExchange(ref _maximumConcurrency, active, observed) == observed)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(80, ct);
                return SearchResult();
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request);
    }
}
