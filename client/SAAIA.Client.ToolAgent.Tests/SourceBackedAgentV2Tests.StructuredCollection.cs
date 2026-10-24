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
}
