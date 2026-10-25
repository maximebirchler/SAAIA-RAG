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

    [Theory]
    [InlineData("Cuisine")]
    [InlineData("Catalogue commercial")]
    [InlineData("")]
    public async Task RouterGridMission_PreservesNavigationStateButLetsTheModelReviseItsResearchScope(string followUpScope)
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
                categoryPath = followUpScope,
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
        Assert.Equal(string.IsNullOrEmpty(followUpScope) ? null : followUpScope, executor.Arguments[1]
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
        Assert.True(followUpCards.Parameters
            .GetProperty("properties")
            .TryGetProperty("categoryPath", out _));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.research_transition.accepted"
            && trace.Fields["decision"] == "switch_to_content_cards"
            && trace.Fields["translated_tool"] == "documents_content_cards");
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
}
