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
}
