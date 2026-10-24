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
}
