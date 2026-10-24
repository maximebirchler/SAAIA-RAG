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
    public async Task CandidateDefinition_UsesFocusedLlmContractAndFlowsIntoAudit()
    {
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "candidate-definition",
                "submit_atomic_candidate_definition",
                new
                {
                    hypotheticalSinglePositionValue = "Tarte aux fruits",
                    candidateObjectType = "recette ou preparation culinaire nommee",
                    candidateEligibilityRule =
                        "Le libelle exact nomme une preparation culinaire autonome, pas un axe ni une rubrique."
                })));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());
        var intake = Intake(
            "Construis un planning du lundi au vendredi avec quatre repas par jour.") with
        {
            CatalogHints = new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine", "Cuisine", 10, Array.Empty<string>())
            }
        };
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "ReviewSemanticCandidateDefinitionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            runner,
            new object?[]
            {
                intake,
                "LIVRABLE: planning\nPREUVES_ATOMIQUES: 20 repas quotidien",
                "Jour",
                new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Petit-dejeuner"] = "Valeur adaptee au premier repas de la journee.",
                    ["Dejeuner"] = "Valeur adaptee au repas du milieu de journee.",
                    ["Collation"] = "Valeur adaptee a une prise alimentaire legere.",
                    ["Souper"] = "Valeur adaptee au repas du soir."
                },
                CancellationToken.None
            }));
        await task;
        var outcome = task.GetType().GetProperty("Result")!.GetValue(task)!;

        Assert.Single(llm.Requests);
        Assert.All(llm.ToolSets, Assert.Empty);
        Assert.Equal(
            new[] { "source_backed_atomic_candidate_definition_v7" },
            llm.StructuredOutputContracts.Select(static contract => contract.Name));
        Assert.Contains("hypotheticalSinglePositionValue", RequestText(llm, 0));
        Assert.Contains("PERIMETRES_DU_CATALOGUE", RequestText(llm, 0));
        Assert.Contains("Cuisine", RequestText(llm, 0));
        Assert.Contains("AXE_LIGNES", RequestText(llm, 0));
        Assert.Contains("ROLES_SEMANTIQUES_DES_COLONNES", RequestText(llm, 0));
        Assert.Contains(
            "Valeur adaptee a une prise alimentaire legere",
            RequestText(llm, 0));
        Assert.Contains("COUVERTURE DE TOUS LES ROLES", RequestText(llm, 0));
        Assert.Contains("LIGNE_DE_REFERENCE", RequestText(llm, 0));
        Assert.Contains("DESCRIPTION_DU_LIVRABLE", RequestText(llm, 0));
        Assert.Contains("Distingue explicitement l'unite de reponse", RequestText(llm, 0));
        Assert.Contains("une fiche ou une section consacree a UNE instance", RequestText(llm, 0));
        Assert.DoesNotContain("PREUVES_ATOMIQUES: 20 repas quotidien", RequestText(llm, 0));
        Assert.DoesNotContain(
            llm.ToolSets.SelectMany(static tools => tools),
            static tool => tool.Name is "documents_content_cards" or "rag_search");
        Assert.Equal(
            "recette ou preparation culinaire nommee",
            outcome.GetType().GetProperty("CandidateObjectType")!.GetValue(outcome));
        Assert.True((bool)outcome.GetType().GetProperty("ProtocolValid")!.GetValue(outcome)!);

        var directInitialMessagesMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildDirectInitialResearchActionMessages",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(directInitialMessagesMethod);
        var directInitialMessages = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentMessage>>(
            directInitialMessagesMethod!.Invoke(
                null,
                new object?[]
                {
                    intake,
                    "LIVRABLE: planning\nPREUVES_ATOMIQUES: 20 repas quotidien",
                    outcome,
                    new[] { "Cuisine" },
                    1,
                    string.Empty,
                    null
                }));
        var directInitialPrompt = string.Join(
            "\n",
            directInitialMessages.Select(static message => message.Content));
        Assert.Contains(
            "TYPE_D_OBJET_SOURCE_CIBLE_DECIDE_PAR_LE_LLM: "
            + "recette ou preparation culinaire nommee",
            directInitialPrompt);
        Assert.Contains(
            "CRITERE_D_ELIGIBILITE_DECIDE_PAR_LE_LLM: "
            + "Le libelle exact nomme une preparation culinaire autonome",
            directInitialPrompt);
        Assert.DoesNotContain(
            "PREUVES_ATOMIQUES: 20 repas quotidien",
            directInitialPrompt);

        var auditRuleMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildCandidateDefinitionAuditRule",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(auditRuleMethod);
        var auditRule = Assert.IsType<string>(auditRuleMethod.Invoke(
            null,
            new[] { outcome }));
        var auditMessagesMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildCandidateBatchAuditMessages",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(auditMessagesMethod);
        var auditMessages = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentMessage>>(auditMessagesMethod.Invoke(
            null,
            new object?[]
            {
                intake,
                "LIVRABLE: planning\nPREUVES_ATOMIQUES: 20 repas quotidien",
                "recette ou preparation culinaire nommee",
                auditRule,
                "Jour",
                new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
                new[] { "Petit-dejeuner", "Dejeuner", "Collation", "Souper" },
                new[]
                {
                    new EvidenceItem(
                        "E1",
                        "canonical_content_card",
                        "documents.content_cards",
                        string.Empty,
                        "doc-1",
                        "test.pdf",
                        "Cuisine/test.pdf",
                        null,
                        null,
                        1,
                        1,
                        "card-1",
                        "Recette complete de tarte aux fraises.",
                        "recette complete de tarte aux fraises",
                        null,
                        1,
                        "Cuisine",
                        "fr",
                        "fr",
                        "high",
                        null,
                        new Dictionary<string, string>
                        {
                            ["sourceAnchorLabel"] = "Tarte aux fraises",
                            ["kind"] = "recipe"
                        },
                        new Dictionary<string, string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>())
                },
                false
            }));
        var auditPrompt = string.Join(
            Environment.NewLine,
            auditMessages.Select(static message => message.Content));
        Assert.Contains("recette ou preparation culinaire nommee", auditPrompt);
        Assert.Contains("preparation culinaire autonome", auditPrompt);
    }
}
