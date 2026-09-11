using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildDirectInitialResearchActionMessages(
            SourceBackedIntake intake,
            string semanticPlan,
            SemanticCandidateDefinitionOutcome candidateDefinition,
            IReadOnlyList<string> permittedCategoryPaths,
            int attempt,
            string failureReason,
            SourceBackedAgentCompletion? previousCompletion)
    {
        var context = new StringBuilder();
        context.Append("DEMANDE_ORIGINALE: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 700));
        context.AppendLine("MISSION_DEJA_INTERPRETEE:");
        var semanticPlanLines = semanticPlan
                     .Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries
                         | StringSplitOptions.TrimEntries);
        var hasFocusedCandidateDefinition =
            candidateDefinition.Attempted
            && candidateDefinition.ProtocolValid;
        foreach (var line in semanticPlanLines.Where(line =>
                     line.StartsWith(
                         "LIVRABLE:",
                         StringComparison.OrdinalIgnoreCase)
                     || !hasFocusedCandidateDefinition
                     && line.StartsWith(
                         "PREUVES_ATOMIQUES:",
                         StringComparison.OrdinalIgnoreCase)))
        {
            context.AppendLine(TrimPromptValue(line, 260));
        }
        var minimumEvidenceCount = ReadMinimumEvidenceCount(semanticPlanLines);
        if (minimumEvidenceCount is > 0)
        {
            context.Append("NOMBRE_MINIMUM_DE_PREUVES_DISTINCTES: ")
                .AppendLine(minimumEvidenceCount.Value.ToString());
        }
        if (candidateDefinition.Attempted
            && candidateDefinition.ProtocolValid)
        {
            context.Append("TYPE_D_OBJET_SOURCE_CIBLE_DECIDE_PAR_LE_LLM: ")
                .AppendLine(TrimPromptValue(
                    candidateDefinition.CandidateObjectType,
                    180));
            context.Append("CRITERE_D_ELIGIBILITE_DECIDE_PAR_LE_LLM: ")
                .AppendLine(TrimPromptValue(
                    candidateDefinition.CandidateEligibilityRule,
                    300));
        }
        context.AppendLine(
            "AUCUNE_OBSERVATION_DOCUMENTAIRE_N_A_ENCORE_ETE_LUE.");
        if (permittedCategoryPaths.Count > 0)
        {
            context.AppendLine("PERIMETRES_CATALOGUE_EXACTS:");
            foreach (var categoryPath in permittedCategoryPaths.Take(40))
                context.Append("- ").AppendLine(TrimPromptValue(categoryPath, 120));
        }
        if (attempt > 1)
        {
            context.Append("REPARATION_MECANIQUE: ")
                .AppendLine(TrimPromptValue(failureReason, 180));
            if (previousCompletion is not null)
            {
                context.AppendLine(TrimPromptValue(
                    RenderSemanticPlanCompletion(previousCompletion),
                    420));
            }
        }
        context.AppendLine(
            "Appelle uniquement submit_initial_observation_decision, sans texte.");

        return new[]
        {
            SourceBackedAgentMessage.System(
                """
                Tu choisis la premiere observation documentaire d'une mission deja
                interpretee. Ne reponds pas et ne reinterprete pas le livrable.

                Choisis une seule capacite. La suite sera decidee apres lecture de
                cette observation; ne prevois donc aucun outil de secours maintenant.

                - documents_navigation_entries observe rapidement les entrees
                  structurees de sommaire ou d'index. Elles nomment souvent des objets
                  complets, mais devront ensuite etre materialisees en preuves.
                - documents_navigation_titles observe les autres titres et ancres.
                - documents_navigation_all observe les deux familles d'ancres.
                - documents_content_cards_representative observe des cartes citables
                  reparties dans les documents lorsque les noms sont inconnus.
                - documents_content_cards_ordered observe les cartes citables dans
                  l'ordre des sources.
                - rag_search cherche un passage lorsque des termes source precis sont
                  deja connus.

                query contient seulement des termes susceptibles d'apparaitre dans
                une source individuelle. Laisse-la vide pour inventorier des noms
                inconnus. rag_search exige une query non vide.

                Lorsque TYPE_D_OBJET_SOURCE_CIBLE_DECIDE_PAR_LE_LLM et
                CRITERE_D_ELIGIBILITE_DECIDE_PAR_LE_LLM sont affiches, ils constituent
                la cible semantique deja arbitree. Utilise-les pour comparer le rendement
                attendu des capacites et formuler, si utile, une query de type d'objet
                source. Ne reviens pas au vocabulaire des axes du livrable et n'invente
                aucun nom d'instance.

                coverageBudget exprime ton estimation du risque de rejet:
                no_rejection_expected, some_rejections_expected ou
                rejection_rate_unknown_or_high. Le premier ne laisse aucune reserve:
                ne le choisis que si chaque element observe devrait etre une preuve
                finale admissible. Des ancres de navigation non citables, des titres
                structurels ou une classe encore inconnue impliquent des rejets
                possibles. Le logiciel traduira mecaniquement ton estimation selon le
                minimum requis et le budget materiel disponible.

                Si un seul perimetre est affiche, il est deja decide par le routeur.
                S'il y en a plusieurs, choisis le plus etroit qui couvre la preuve
                atomique; la chaine vide signifie corpus complet.

                Appelle uniquement submit_initial_observation_decision.
                """),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
    }

    private static int? ReadMinimumEvidenceCount(IEnumerable<string> semanticPlanLines)
    {
        var line = semanticPlanLines.FirstOrDefault(static candidate =>
            candidate.StartsWith(
                "PREUVES_ATOMIQUES:",
                StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var separator = line.IndexOf(':');
        if (separator < 0 || separator + 1 >= line.Length)
            return null;

        var firstToken = line[(separator + 1)..]
            .TrimStart()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return int.TryParse(firstToken, out var count) && count > 0
            ? count
            : null;
    }
}
