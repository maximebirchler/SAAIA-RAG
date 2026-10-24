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
}
