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
}
