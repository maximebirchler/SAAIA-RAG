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
}
