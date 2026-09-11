using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildSemanticCandidateStrategyMessages(
            SourceBackedIntake intake,
            string semanticPlan,
            string rowHeader,
            IReadOnlyList<string> rowLabels,
            IReadOnlyList<string> columnLabels,
            IReadOnlyList<string> categoryPaths,
            int requiredAtomicEvidenceCount,
            int attempt,
            string failureReason,
            SourceBackedAgentCompletion? previousCompletion)
    {
        var user = new StringBuilder()
            .AppendLine("DEMANDE:")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 700))
            .Append("NOMBRE_MINIMUM: ")
            .AppendLine(requiredAtomicEvidenceCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            .Append("AXES_DU_LIVRABLE_A_NE_PAS_RECOPIER_COMME_OBJET_SOURCE: ")
            .AppendLine(string.Join(
                " | ",
                new[] { rowHeader }.Concat(rowLabels).Concat(columnLabels)));
        user.AppendLine("PERIMETRES_EXACTS:")
            .AppendLine("- 0: corpus complet");
        for (var index = 0; index < categoryPaths.Count; index++)
        {
            user.Append("- ")
                .Append(index + 1)
                .Append(": ")
                .AppendLine(TrimPromptValue(categoryPaths[index], 100));
        }
        if (attempt > 1)
        {
            user.Append("REPARATION_MECANIQUE: ")
                .AppendLine(TrimPromptValue(failureReason, 180));
            if (previousCompletion is not null)
            {
                user.Append("SORTIE_PRECEDENTE: ")
                    .AppendLine(TrimPromptValue(
                        RenderSemanticPlanCompletion(previousCompletion),
                        500));
            }
        }
        user.AppendLine(
            "Choisis seulement la premiere observation et appelle "
            + SemanticCandidateStrategyToolName + ".");

        return new[]
        {
            SourceBackedAgentMessage.System(
                """
                Tu proposes une hypothese de recherche et sa premiere observation
                documentaire. Ne reponds pas et ne classes encore aucun candidat.

                candidateObjectType nomme le genre editorial ou catalogue d'UNE entree
                source autonome, nommee et titree qui peut remplir seule une position
                finale porteuse de valeur. Retire mentalement tous les axes du livrable:
                le type doit rester valable. Ce n'est jamais le livrable assemble, une
                grille, un axe, une ligne, une colonne, une periode, un role, une case ou
                une valeur abstraite. Cette hypothese est semantique et revisable apres
                observation du corpus.

                candidateEligibilityRule decrit brievement ce que le libelle exact doit
                nommer et distingue une instance autonome des documents, rubriques,
                collections, champs et fragments. N'ajoute ni coordonnee de placement,
                ni nombre demande, ni regle de presentation.

                documents_content_cards fournit des entrees citables mais heterogenes.
                documents_navigation_entries parcourt sommaires et index resolus;
                documents_navigation_titles parcourt les autres titres;
                documents_navigation_all compare les deux. Une entree de navigation
                cartographie le corpus mais n'est pas encore une preuve. rag_search exige
                des termes sources precis deja formulables. Choisis la capacite la plus
                informative et economique selon la demande, sans quota d'outils impose.

                sourceDiscoveryQuery contient des termes de source, jamais la forme du
                tableau ni une reponse inventee; elle peut etre vide pour un inventaire.
                Choisis le perimetre exact le plus etroit affichant le bon domaine, sinon
                0. shared_pool signifie qu'un meme inventaire alimente plusieurs positions;
                partitioned_pool exige des corpus ou classes reellement disjoints.
                initialLimit est un budget candidat au moins egal a NOMBRE_MINIMUM, avec
                une marge utile choisie par toi. Appelle uniquement
                submit_candidate_evidence_strategy.
                """),
            SourceBackedAgentMessage.User(user.ToString().Trim())
        };
    }

    private static SourceBackedAgentToolDefinition
        BuildSemanticCandidateStrategyTool(
            int categoryPathCount,
            int requiredAtomicEvidenceCount,
            int maximumWorkingEvidenceItems)
    {
        var maximum = Math.Max(1, maximumWorkingEvidenceItems);
        var minimum = Math.Clamp(
            requiredAtomicEvidenceCount,
            1,
            maximum);
        return new SourceBackedAgentToolDefinition(
            SemanticCandidateStrategyToolName,
            "Soumet uniquement la premiere observation documentaire choisie.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    candidateObjectType = new
                    {
                        type = "string",
                        description =
                            "Genre concret d'entree documentaire autonome, titree et citable a retrouver dans le corpus; jamais une case ou un role du livrable.",
                        minLength = 2,
                        maxLength = 80
                    },
                    candidateEligibilityRule = new
                    {
                        type = "string",
                        description =
                            "Frontiere semantique intrinseque et concise entre une instance autonome du type cible et une rubrique, collection, position ou fragment; sans coordonnee de placement.",
                        minLength = 8,
                        maxLength = 240
                    },
                    initialCapability = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "documents_content_cards",
                            "documents_navigation_entries",
                            "documents_navigation_titles",
                            "documents_navigation_all",
                            "rag_search"
                        }
                    },
                    sourceDiscoveryQuery = new
                    {
                        type = "string",
                        minLength = 0,
                        maxLength = 120
                    },
                    initialLimit = new
                    {
                        type = "integer",
                        minimum,
                        maximum
                    },
                    candidateScopeIds = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "integer",
                            @enum = Enumerable.Range(
                                    0,
                                    categoryPathCount + 1)
                                .ToArray()
                        },
                        minItems = 1,
                        maxItems = Math.Max(
                            1,
                            Math.Min(4, categoryPathCount + 1)),
                        uniqueItems = true
                    },
                    candidatePoolRelation = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            SharedCandidatePoolRelation,
                            PartitionedCandidatePoolRelation
                        }
                    }
                },
                required = new[]
                {
                    "candidateObjectType",
                    "candidateEligibilityRule",
                    "initialCapability",
                    "sourceDiscoveryQuery",
                    "initialLimit",
                    "candidateScopeIds",
                    "candidatePoolRelation"
                },
                additionalProperties = false
            }, ClientJson.CamelCase));
    }
}
