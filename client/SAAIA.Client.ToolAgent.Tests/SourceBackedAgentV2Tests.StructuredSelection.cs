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
}
