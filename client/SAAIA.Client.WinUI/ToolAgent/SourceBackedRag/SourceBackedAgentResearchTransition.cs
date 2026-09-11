using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record ResearchTransitionExecution(
        SourceBackedAgentCompletion Completion,
        bool Attempted,
        bool ProtocolValid,
        string ActionId,
        int LlmCallCount,
        string FailureReason);

    private static bool UsesResearchTransitionTools(
        IReadOnlyList<SourceBackedAgentToolDefinition> tools)
        => tools.Count > 0
           && tools.All(static tool => IsResearchTransitionToolName(tool.Name)
                                       || tool.Name == "documents_context");

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildResearchTransitionDecisionMessages(
            SourceBackedIntake intake,
            string semanticPlan,
            string candidateObjectType,
            string candidateEligibilityRule,
            EvidenceBundle bundle,
            IReadOnlySet<string> resolvedNavigationEvidenceIds,
            IReadOnlyList<RetrievalRequest> executedRequests,
            IReadOnlyList<string> activeScopePaths,
            int approvedSourceCount,
            int requiredEvidenceCount,
            string? semanticFeedback,
            int maximumWorkingEvidenceItems,
            bool compactForContextRecovery)
    {
        if (compactForContextRecovery)
        {
            return BuildCompactResearchTransitionDecisionMessages(
                intake,
                semanticPlan,
                candidateObjectType,
                candidateEligibilityRule,
                bundle,
                resolvedNavigationEvidenceIds,
                executedRequests,
                activeScopePaths,
                approvedSourceCount,
                requiredEvidenceCount,
                semanticFeedback,
                maximumWorkingEvidenceItems);
        }

        var context = new StringBuilder();
        context.Append("OBJECTIF UTILISATEUR: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 420));
        context.Append("BESOIN DOCUMENTAIRE: ")
            .Append(approvedSourceCount)
            .Append(" preuves approuvees sur ")
            .Append(requiredEvidenceCount)
            .AppendLine(" attendues.");
        context.Append("MISSION LLM: ")
            .AppendLine(TrimPromptValue(semanticPlan, 520));
        if (!string.IsNullOrWhiteSpace(candidateObjectType))
        {
            context.Append("TYPE DE PREUVE ATOMIQUE DEJA DECIDE PAR LE LLM: ")
                .AppendLine(TrimPromptValue(candidateObjectType, 180));
        }
        if (!string.IsNullOrWhiteSpace(candidateEligibilityRule))
        {
            context.Append("REGLE D'ELIGIBILITE DEJA DECIDEE PAR LE LLM: ")
                .AppendLine(TrimPromptValue(candidateEligibilityRule, 360));
        }
        var semanticRoleInventory = BuildLlmAuthoredRoleInventoryContext(
            intake,
            bundle,
            maximumWorkingEvidenceItems);
        if (!string.IsNullOrWhiteSpace(semanticRoleInventory))
        {
            context.AppendLine(semanticRoleInventory);
        }
        if (activeScopePaths.Count > 0)
        {
            context.Append("PERIMETRES ACTIFS DECIDES PAR LE LLM: ")
                .AppendLine(string.Join(
                    " | ",
                    activeScopePaths.Select(static path =>
                        string.IsNullOrWhiteSpace(path)
                            ? "corpus complet"
                            : path)));
        }
        context.AppendLine("ROUTES PAGINEES ENCORE OUVERTES:");
        context.AppendLine(DescribePaginationContinuationOptions(
            executedRequests,
            Math.Min(5, Math.Max(1, maximumWorkingEvidenceItems))));
        context.AppendLine("RENDEMENT DES DERNIERES ACTIONS:");
        foreach (var request in executedRequests.TakeLast(8))
        {
            context.Append("- ")
                .Append(request.ToolName)
                .Append(" | requete=")
                .Append(string.IsNullOrWhiteSpace(request.Query)
                    ? "vide"
                    : TrimPromptValue(request.Query, 90))
                .Append(" | offset=")
                .Append(request.Offset)
                .Append(" | nouvelles_preuves=")
                .Append(request.NewEvidenceCount)
                .Append(" | preuves_materialisees=")
                .Append(request.MaterializedEvidenceCount);
            if (request.SemanticAuditApprovedCount is not null
                || request.SemanticAuditRejectedCount is not null)
            {
                context.Append(" | audit_llm_approuvees=")
                    .Append(request.SemanticAuditApprovedCount.GetValueOrDefault())
                    .Append(" | audit_llm_refusees=")
                    .Append(request.SemanticAuditRejectedCount.GetValueOrDefault());
            }
            if (request.SemanticCompatibilityCounts is { Count: > 0 })
            {
                context.Append(" | roles_compatibles_decides_par_le_llm=")
                    .Append(string.Join(
                        ",",
                        request.SemanticCompatibilityCounts
                            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(static pair => pair.Key + "+" + pair.Value)));
            }
            if (request.NewEvidenceCount == 0)
                context.Append(" | rendement=aucune_nouvelle_preuve");
            if (request.NextOffset is >= 0)
            {
                context.Append(" | nextOffset=")
                    .Append(request.NextOffset);
            }
            context.AppendLine();
        }

        var retrievalConditions = BuildObservedRetrievalConditions(bundle, 8);
        if (retrievalConditions.Length > 0)
            context.AppendLine(retrievalConditions);
        context.AppendLine(BuildDocumentFocusInventory(
            bundle,
            maximumWorkingEvidenceItems));

        context.AppendLine("ANCRES DE NAVIGATION VISIBLES NON CITABLES:");
        foreach (var evidenceId in SelectPendingNavigationEvidenceIds(
                     bundle,
                     resolvedNavigationEvidenceIds,
                     maximumWorkingEvidenceItems,
                     requireResolvableTarget: false))
        {
            if (!bundle.ById.TryGetValue(evidenceId, out var item))
                continue;
            context.Append("- ")
                .Append(evidenceId)
                .Append(" | ")
                .Append(TrimPromptValue(
                    GetEvidenceDisplayValue(item),
                    100));
            var document = item.DocPath ?? item.DocName ?? item.DocId;
            if (!string.IsNullOrWhiteSpace(document))
            {
                context.Append(" | document=")
                    .Append(TrimPromptValue(document, 140));
            }
            if (item.PageStart is > 0)
                context.Append(" | page=").Append(item.PageStart.Value);
            context.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(semanticFeedback))
        {
            context.AppendLine("BESOIN SEMANTIQUE RESTANT DECIDE PAR LE JUGE LLM:")
                .AppendLine(TrimPromptValue(semanticFeedback, 1200));
            context.AppendLine(
                "TRANSFORMATION DU BESOIN EN RECHERCHE: si des cellules ont ete refusees, "
                + "derive les proprietes intrinseques des nouvelles instances depuis leur role, "
                + "leur valeur refusee et le motif du juge. Une source qui explique seulement "
                + "comment construire le livrable n'apporte pas les instances manquantes. La query "
                + "peut reunir des noms d'instances que tu estimes plausibles dans les sources pour "
                + "le role deficient; ces hypotheses servent seulement a decouvrir des cartes et ne "
                + "les approuvent jamais. Evite les candidats deja visibles dans le vivier, ou laisse "
                + "la query vide pour explorer un inventaire large.");
            context.AppendLine(
                "FIDELITE STRICTE: chaque propriete de recherche doit etre explicitement presente "
                + "dans la demande, le role canonique ou le motif du juge. N'ajoute aucune contrainte "
                + "alimentaire, sanitaire, d'audience, de lieu, de cout ou de style. Un qualificatif "
                + "portant sur le format, le ton ou la presentation du livrable ne decrit pas les "
                + "preuves atomiques.");
            context.AppendLine(
                "QUERY LEXICALE D'INSTANCES: privilegie un petit ensemble de noms courts d'instances "
                + "susceptibles d'etre les titres des preuves recherchees. Le libelle du role, une "
                + "liste de composants, la periode ou la forme du livrable retrouvent souvent des "
                + "rubriques plutot que des instances. Si tu ne peux pas deriver de noms plausibles "
                + "sans ajouter une contrainte, prefere une route d'inventaire avec query vide.");
        }
        context.AppendLine(
            "CONTINUITE SEMANTIQUE: les lignes, colonnes, periodes, roles et autres "
            + "axes du livrable servent a placer les preuves plus tard. Une source "
            + "candidate n'a pas a contenir ces axes. Si un libelle visible nomme "
            + "deja une instance concrete du type PREUVES_ATOMIQUES, son absence de "
            + "coordonnees du livrable ne le rend pas inadequat.");
        context.AppendLine(
            "LIBERTE DE PIVOT: tu n'as jamais a epuiser une route ni a resoudre toutes "
            + "les ancres visibles. Les preuves deja approuvees restent disponibles. "
            + "Changer de route est une priorisation selon le besoin restant, le rendement "
            + "marginal attendu et le temps; ce n'est pas un rejet semantique individuel "
            + "de chaque ancre laissee non resolue.");
        context.AppendLine(
            "CONTEXTE DU MEME DOCUMENT: si les faits encore manquants sont des passages "
            + "complementaires d'un item ou document citable deja visible, prefere "
            + "expand_document_context avec son EvidenceId. Si le document est deja le bon "
            + "mais que la page du contenu semantique manquant est inconnue, choisis "
            + "refine_focused_document_search avec l'EvidenceId du document. La navigation "
            + "focalisee ne cherche que sa structure, ses titres, son sommaire et son index; "
            + "elle ne recherche pas le corps par similarite semantique. Les routes globales "
            + "servent a chercher un autre item ou document.");
        context.AppendLine(
            "Appelle directement un seul des outils exposes avec ses arguments. "
            + "Ne reponds pas et ne planifie aucun outil de secours avant d'avoir lu "
            + "la prochaine observation.");

        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es l'orchestrateur semantique du RAG. Compare le besoin restant, "
                + "le rendement documentaire mesure et les pistes visibles, puis choisis "
                + "une seule prochaine action et fournis directement ses arguments avec "
                + "l'outil correspondant. Une continuation conserve exactement la route et "
                + "atteint sa page inedite; une nouvelle route recommence a offset 0. "
                + "La navigation oriente mais ne prouve rien. Tu peux materialiser un "
                + "sous-ensemble utile puis completer le vivier plus tard. Le code ne choisit "
                + "pas a ta place. Recherche le meilleur rendement marginal sans sacrifier "
                + "la qualite documentaire. Juge le rendement des libelles reellement visibles: "
                + "une route initialement non filtree peut deja etre excellente. Si assez de "
                + "libelles visibles nomment des candidats utiles, les resoudre conserve leurs "
                + "identites exactes; une nouvelle route les abandonne et observe autre chose. "
                + "Ne change donc pas de route uniquement parce que la requete precedente etait vide. "
                + "Conserve le type de preuve atomique et la regle d'eligibilite que tu as "
                + "deja definis: une query doit retrouver des instances de ce type, pas une "
                + "classe voisine qui sera ensuite refusee par ton propre audit. "
                + "Un role canonique deficient peut guider tes hypotheses de noms d'instances, "
                + "mais ne concatene jamais les axes ou la forme du livrable en query documentaire. "
                + "N'invente aucune contrainte pour rendre la query plus specifique; une query vide "
                + "est preferable a une hypothese qui change le besoin."),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
    }

    private static string BuildLlmAuthoredRoleInventoryContext(
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        int maximumWorkingEvidenceItems)
    {
        var semanticRoles = intake.CanonicalColumnSemanticRoles;
        if (semanticRoles is null || semanticRoles.Count == 0)
            return string.Empty;

        var roles = semanticRoles
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToArray();
        if (roles.Length == 0)
            return string.Empty;

        var annotatedCandidates = bundle.Items
            .Where(static item => IsMechanicallyCitableCandidate(item))
            .Select(item => new
            {
                Item = item,
                HasCompatibility = TryReadCompatibleColumnLabels(
                    item,
                    out var compatibleColumns),
                CompatibleColumns = compatibleColumns
            })
            .Where(static candidate => candidate.HasCompatibility)
            .DistinctBy(
                static candidate => candidate.Item.VisibleSourceKey,
                StringComparer.OrdinalIgnoreCase)
            .DistinctBy(
                static candidate => GetEvidenceDisplayValue(candidate.Item),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var visibleCandidatesPerRole = Math.Clamp(
            maximumWorkingEvidenceItems / Math.Max(1, roles.Length),
            3,
            6);
        var context = new StringBuilder();
        context.AppendLine("ROLES SEMANTIQUES CANONIQUES DEJA DECIDES PAR LE LLM:");
        foreach (var role in roles)
        {
            context.Append("- ")
                .Append(TrimPromptValue(role.Key, 90))
                .Append(" => ")
                .AppendLine(TrimPromptValue(role.Value, 220));
        }

        context.AppendLine(
            "VIVIER APPROUVE ET COMPATIBILITES DEJA DECIDEES PAR LE LLM:");
        foreach (var role in roles)
        {
            var compatibleCandidates = annotatedCandidates
                .Where(candidate => candidate.CompatibleColumns.Contains(
                    role.Key,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
            context.Append("- ")
                .Append(TrimPromptValue(role.Key, 90))
                .Append(" | capacite=")
                .Append(compatibleCandidates.Length)
                .Append(" | candidats=");
            if (compatibleCandidates.Length == 0)
            {
                context.AppendLine("AUCUN");
                continue;
            }
            context.AppendLine(string.Join(
                " ; ",
                compatibleCandidates
                    .Take(visibleCandidatesPerRole)
                    .Select(static candidate =>
                        candidate.Item.EvidenceId
                        + " | "
                        + TrimPromptValue(
                            GetEvidenceDisplayValue(candidate.Item),
                            72))));
        }
        var withoutCompatibleRole = annotatedCandidates.Count(
            static candidate => candidate.CompatibleColumns.Count == 0);
        if (withoutCompatibleRole > 0)
        {
            context.Append("- APPROUVES SANS ROLE COMPATIBLE | capacite=")
                .AppendLine(withoutCompatibleRole.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        }
        context.Append(
            "Ces compatibilites sont ta memoire semantique, pas une decision du code. "
            + "Utilise-la pour cibler seulement les lacunes et eviter de rechercher ou "
            + "reutiliser inutilement les memes instances.");
        return context.ToString().TrimEnd();
    }

    private static ResearchTransitionExecution CompleteResearchTransition(
        SourceBackedAgentCompletion completion)
    {
        if (completion.ToolCalls.Count != 1
            || !IsResearchTransitionToolName(completion.ToolCalls[0].Name))
        {
            return new ResearchTransitionExecution(
                completion,
                false,
                true,
                string.Empty,
                0,
                string.Empty);
        }

        return new ResearchTransitionExecution(
            completion,
            true,
            true,
            completion.ToolCalls[0].Name,
            1,
            string.Empty);
    }
}
