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
