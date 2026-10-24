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
    public async Task CompactState_PrioritizesRecentCanonicalCardsAndPreservesPathAndCategory()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: options sourcees."),
            Completion(Call("old", "rag_search", new { query = "options anciennes" })),
            Completion(Call("new", "rag_search", new { query = "options recentes" })),
            Completion("L'option recente est utilisable [E8]."),
            SemanticReview("accept", "L'option recente est directement soutenue."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Cuisine/Ancien.pdf", "Cuisine", "old", 1, 4),
            SearchResultWithCards("Cuisine/Recent.pdf", "Cuisine", "new", 8, 2));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with { MaximumWorkingEvidenceItems = 2 });

        await runner.RunAsync(Intake("Donne une option documentee."), CancellationToken.None);

        var compactState = RequestText(llm, 3);
        Assert.Contains("Cuisine/Recent.pdf", compactState);
        Assert.Contains("D1 | docPath=Cuisine/Recent.pdf", compactState);
        Assert.Contains("categorie=Cuisine", compactState);
        Assert.Equal(
            1,
            compactState.Split(
                "Cuisine/Recent.pdf",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("new-card-2", compactState);
        Assert.DoesNotContain("Cuisine/Ancien.pdf", compactState);
        Assert.Contains("2 conservees sur 6 citables observees", compactState);
        Assert.Contains("sources visibles distinctes", compactState);
        Assert.Contains("groupe_source=V", compactState);
    }

    [Fact]
    public async Task CompactState_DoesNotHideRecentSearchEvidenceBehindOlderCardInventory()
    {
        var inventoryCall = Call("cards", "documents_content_cards", new
        {
            categoryPath = "Cuisine",
            limit = 40,
            offset = 0
        });
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une proposition sourcee."),
            Completion(inventoryCall),
            Completion(Call("search", "rag_search", new
            {
                query = "dessert chocolat"
            })),
            Completion("Le resultat de recherche recent repond directement [E21]."),
            SemanticReview("accept", "E21 soutient directement la proposition."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(TwentyMealCards()),
                SearchResult()),
            Options() with { MaximumWorkingEvidenceItems = 4 });

        var result = await runner.RunAsync(
            Intake("Propose un dessert au chocolat."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var compactState = RequestText(llm, 3);
        Assert.Contains("E21 | groupe_source=", compactState);
        Assert.Contains("85 N", compactState);
        Assert.Contains("HydraulicPumpManual.pdf", compactState);
        Assert.DoesNotContain("recette-card-1", compactState);
    }

    [Fact]
    public async Task CompactState_RepresentsDistinctSourcesBeforeSameSourceAlternatives()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: une option sourcee\n"
                + "PREUVES_ATOMIQUES: 1 instance\n"
                + "INTENTIONS_RECHERCHE: option\n"
                + "APPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: une option\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune option"),
            Completion(Call("search", "rag_search", new
            {
                query = "options"
            })),
            Completion("L'option la plus recente est documentee [E5]."),
            SemanticReview(
                "accept",
                "E5 soutient directement l'option retenue."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(CompactDiversitySearchResult()),
            Options() with { MaximumWorkingEvidenceItems = 3 });

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var compactState = RequestText(llm, 2);
        Assert.Contains("- E5 |", compactState);
        Assert.Contains("- E2 |", compactState);
        Assert.Contains("- E1 |", compactState);
        Assert.DoesNotContain("- E4 |", compactState);
        Assert.DoesNotContain("- E3 |", compactState);
        Assert.Contains("3 conservees sur 5 citables observees", compactState);
    }
}
