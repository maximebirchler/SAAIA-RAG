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
}
