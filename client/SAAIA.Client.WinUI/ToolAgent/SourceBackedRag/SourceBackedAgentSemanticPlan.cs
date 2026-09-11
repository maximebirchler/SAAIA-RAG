using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SemanticPlanToolName = "submit_semantic_plan";

    private sealed record SemanticPlanPreparation(
        string Plan,
        SourceBackedAgentCompletion Completion,
        string RowHeader,
        IReadOnlyList<string> RowLabels,
        IReadOnlyDictionary<string, string> ColumnRoles,
        bool StructuredProtocolEnabled,
        bool ProtocolValid,
        int Attempts,
        string FailureReason,
        string DecisionSource = "semantic_planner",
        SourceBackedInitialToolCall? InitialToolCall = null,
        string InitialMissionFailureReason = "");

    private async Task<SemanticPlanPreparation> PrepareSemanticPlanAsync(
        SourceBackedIntake intake,
        CancellationToken ct)
    {
        var plannerDecisionSource = "semantic_planner";
        var initialMissionFailureReason = string.Empty;
        if (intake.InitialSemanticMission is { } initialMission)
        {
            var routerCompletion = new SourceBackedAgentCompletion(
                string.Empty,
                new[]
                {
                    new SourceBackedAgentToolCall(
                        "router-semantic-mission",
                        SemanticPlanToolName,
                        initialMission.Arguments)
                },
                "tool_calls",
                0,
                0);
            if (TryReadStructuredSemanticPlan(
                    routerCompletion,
                    out var routerPlan,
                    out var routerRowHeader,
                    out var routerRowLabels,
                    out var routerColumnRoles,
                    out var routerMissionContractError))
            {
                return new SemanticPlanPreparation(
                    routerPlan,
                    routerCompletion with { Content = routerPlan },
                    routerRowHeader,
                    routerRowLabels,
                    routerColumnRoles,
                    StructuredProtocolEnabled: true,
                    ProtocolValid: true,
                    Attempts: 0,
                    FailureReason: string.Empty,
                    DecisionSource: initialMission.DecisionSource,
                    InitialToolCall: TryBuildSemanticPlanInitialToolCall(
                        intake,
                        routerCompletion,
                        initialMission.DecisionSource));
            }

            initialMissionFailureReason = routerMissionContractError;
            plannerDecisionSource =
                "semantic_planner_after_router_mission_rejected";
        }

        if (!_options.StructuredSemanticPlanningEnabled)
        {
            var completion = await _llm.CompleteAsync(
                    new[]
                    {
                        SourceBackedAgentMessage.System(BuildSemanticPlanningPrompt()),
                        SourceBackedAgentMessage.User(BuildUserContext(
                            intake,
                            _options.MaximumContextTokens))
                    },
                    Array.Empty<SourceBackedAgentToolDefinition>(),
                    _options.MaximumPlanningTokens,
                    ct,
                    _options.PlanningTemperature)
                .ConfigureAwait(false);
            var plan = string.IsNullOrWhiteSpace(completion.Content)
                ? BuildUnavailableSemanticPlan()
                : completion.Content.Trim();
            return new SemanticPlanPreparation(
                plan,
                completion,
                string.Empty,
                Array.Empty<string>(),
                EmptySemanticColumnRoles(),
                StructuredProtocolEnabled: false,
                ProtocolValid: !string.IsNullOrWhiteSpace(completion.Content),
                Attempts: 1,
                FailureReason: string.IsNullOrWhiteSpace(completion.Content)
                    ? "planning_content_missing"
                    : string.Empty,
                DecisionSource: plannerDecisionSource,
                InitialMissionFailureReason: initialMissionFailureReason);
        }

        SourceBackedAgentCompletion? previousCompletion = null;
        var failureReason = string.Empty;
        var promptTokens = 0;
        var completionTokens = 0;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var completion = await _llm.CompleteAsync(
                    BuildStructuredSemanticPlanMessages(
                        intake,
                        attempt,
                        failureReason,
                        previousCompletion),
                    new[] { BuildSemanticPlanTool(intake) },
                    _options.MaximumPlanningTokens,
                    ct,
                    _options.PlanningTemperature,
                    requireToolCall: true)
                .ConfigureAwait(false);
            promptTokens += completion.PromptTokens.GetValueOrDefault();
            completionTokens += completion.CompletionTokens.GetValueOrDefault();
            if (TryReadStructuredSemanticPlan(
                    completion,
                    out var plan,
                    out var rowHeader,
                    out var rowLabels,
                    out var columnRoles,
                    out failureReason))
            {
                return new SemanticPlanPreparation(
                    plan,
                    completion with
                    {
                        Content = plan,
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens
                    },
                    rowHeader,
                    rowLabels,
                    columnRoles,
                    StructuredProtocolEnabled: true,
                    ProtocolValid: true,
                    Attempts: attempt,
                    FailureReason: string.Empty,
                    DecisionSource: plannerDecisionSource,
                    InitialToolCall: TryBuildSemanticPlanInitialToolCall(
                        intake,
                        completion,
                        plannerDecisionSource),
                    InitialMissionFailureReason:
                        initialMissionFailureReason);
            }

            previousCompletion = completion;
        }

        return new SemanticPlanPreparation(
            BuildUnavailableSemanticPlan(),
            (previousCompletion ?? new SourceBackedAgentCompletion(
                string.Empty,
                Array.Empty<SourceBackedAgentToolCall>(),
                "protocol_error")) with
            {
                Content = string.Empty,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens
            },
            string.Empty,
            Array.Empty<string>(),
            EmptySemanticColumnRoles(),
            StructuredProtocolEnabled: true,
            ProtocolValid: false,
            Attempts: 2,
            FailureReason: failureReason,
            DecisionSource: plannerDecisionSource,
            InitialMissionFailureReason: initialMissionFailureReason);
    }

    private static IReadOnlyList<SourceBackedAgentMessage> BuildStructuredSemanticPlanMessages(
        SourceBackedIntake intake,
        int attempt,
        string failureReason,
        SourceBackedAgentCompletion? previousCompletion)
    {
        var user = new StringBuilder();
        user.AppendLine("DEMANDE_ORIGINALE:");
        user.AppendLine(TrimPromptValue(intake.UserQuestion, 1000));
        user.AppendLine("LANGUE: " + TrimPromptValue(intake.Language, 20));
        user.AppendLine("TYPE_DE_TACHE: " + TrimPromptValue(intake.TaskKind, 80));
        if (intake.ExplicitConstraints.Count > 0)
        {
            user.AppendLine("CONTRAINTES_EXPLICITES:");
            foreach (var constraint in intake.ExplicitConstraints.Take(12))
                user.AppendLine("- " + TrimPromptValue(constraint, 160));
        }
        var categoryPaths = SourceBackedRetrievalScope
            .GetAvailableCategoryPaths(intake);
        user.AppendLine("PERIMETRES_CATALOGUE:");
        user.AppendLine("- 0: corpus complet");
        for (var index = 0; index < categoryPaths.Count; index++)
        {
            user.Append("- ")
                .Append(index + 1)
                .Append(": ")
                .AppendLine(TrimPromptValue(categoryPaths[index], 100));
        }
        if (attempt > 1)
        {
            user.AppendLine("REPARATION_DU_PROTOCOLE:");
            user.AppendLine(
                "Le plan precedent est invalide (" + TrimPromptValue(failureReason, 180) + ").");
            if (previousCompletion is not null)
            {
                user.AppendLine("SORTIE_PRECEDENTE:");
                user.AppendLine(TrimPromptValue(
                    RenderSemanticPlanCompletion(previousCompletion),
                    700));
            }
        }
        user.AppendLine("Appelle uniquement " + SemanticPlanToolName + ".");

        return new[]
        {
            SourceBackedAgentMessage.System(
                """
                Tu es le planificateur semantique principal d'un agent RAG local. Tu cadres
                la mission sans y repondre, sans chercher de preuve et sans choisir de valeur
                finale. Le code validera seulement ton protocole.

                Preserve exactement les exigences, quantites et axes explicites de la demande.
                N'ajoute aucune preference, qualite, methode, sous-champ ou contrainte absente.
                Une preuve atomique est un objet source autonome, nomme et citable, utilisable
                dans une position du livrable avant son affectation finale. N'integre jamais
                les coordonnees ou libelles d'axes dans son type. Ce n'est jamais une rubrique,
                un sous-composant, un libelle d'axe, une case apres placement ou une collection
                abstraite.

                atomicEvidenceMode vaut named_item lorsqu'un objet nomme par la source occupe
                chaque position, et content_claim lorsqu'une affirmation, regle, exigence,
                definition, constatation ou autre contenu interne a la source l'occupe. Le mode
                ne change ni le nombre ni le type semantique des preuves.

                selectionPolicy vaut single_item pour une unite explicitement singuliere,
                explicit_set pour une quantite explicite, et open_set pour un pluriel ou
                collectif ouvert lorsque choisir un seul objet inventerait une preference.
                open_set utilise deux ou trois preuves atomiques. Utilise
                structured_layout pour un livrable structure.

                structuredLayout vaut true lorsqu'un livrable possede des positions visibles
                formees par un ou deux axes, y compris lorsque chaque position ne contiendra
                finalement qu'un nom court. Ne transforme pas une grille explicitement organisee
                par des libelles repetes en simple liste. rowCount et columnCount comptent les
                positions reelles de chaque axe, pas les mots qui decrivent une plage. Dans ce
                cas, atomicEvidenceCount doit etre exactement rowCount multiplie par columnCount.
                Pour un livrable non structure, structuredLayout vaut false, rowCount et
                columnCount valent 1, et atomicEvidenceCount reste le nombre d'instances
                distinctes demandees.

                Pour un livrable structure, rowLabels contient exactement les libelles
                visibles des lignes, rowHeader nomme leur axe, et columns contient exactement
                les colonnes de donnees en excluant rowHeader, dans leur ordre final.
                Conserve les libelles explicites de la demande dans sa langue, sans leur
                ajouter de description, exemple, valeur, qualite, preference, horaire ou
                detail. Pour un livrable non structure, rowLabels et columns sont vides.

                Ce cadrage fixe seulement la forme et la granularite du livrable. Ne propose
                aucune valeur finale ni critere qualitatif. Choisis la premiere action
                executable la plus economique avec initialCapability, initialQuery,
                initialScopeId et initialLimit. Le futur orchestrateur gardera tous les outils
                et adaptera librement la suite selon les observations.

                Choisis aussi le contrat de sortie le plus petit qui suffit:
                - single_item: exactement une instance, sans grille. Omets structuredLayout,
                  rowCount, columnCount, atomicEvidenceCount, rowHeader, rowLabels et columns;
                - multi_item: plusieurs instances sans grille. Fournis seulement
                  atomicEvidenceCount parmi ces champs optionnels;
                - structured_layout: positions visibles formees par un ou deux axes. Fournis
                  tous les champs de dimensions et d'axes.

                Capacites:
                - documents_content_cards: decouvrir beaucoup d'instances nommees encore
                  inconnues, deja citables par fichier et page, sans deviner leurs noms;
                - documents_navigation: localiser un document, son sommaire ou sa structure;
                  ses pointeurs ne constituent pas des preuves finales;
                - rag_search: chercher des termes sources deja suffisamment precis;
                documents_context reste disponible plus tard, une fois un document, une page
                ou un chunk reellement localise; ne le choisis jamais comme premiere action.

                Compare le cout au besoin reel: pour une seule instance et des termes de
                contenu deja formulables, rag_search est souvent plus direct. Utilise
                documents_content_cards surtout pour decouvrir plusieurs noms inconnus ou
                lorsqu'aucun terme lexical utile n'est encore disponible. Pour un inventaire,
                initialLimit doit laisser une marge pour ecarter rubriques et pointeurs; ce
                n'est pas le nombre final d'instances demandees.

                initialScopeId doit copier l'identifiant numerique d'un PERIMETRES_CATALOGUE.
                Choisis semantiquement le meilleur corpus; 0 signifie corpus complet.
                initialQuery contient des termes susceptibles d'apparaitre dans la source.
                Il peut etre vide pour un inventaire large de documents_content_cards ou une
                navigation large, mais jamais pour rag_search. initialLimit est un budget de
                candidats, pas un objectif de resultats.

                Appelle uniquement submit_semantic_plan, sans texte ni autre outil.
                """),
            SourceBackedAgentMessage.User(user.ToString().Trim())
        };
    }

    private static SourceBackedAgentToolDefinition BuildSemanticPlanTool(
        SourceBackedIntake intake)
        => new(
            SemanticPlanToolName,
            "Soumet le cadrage semantique, les dimensions et le nombre de preuves "
            + "atomiques decides par le LLM, sans rechercher de contenu.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    planKind = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "single_item",
                            "multi_item",
                            "structured_layout"
                        }
                    },
                    deliverable = BoundedStringSchema(2, 220),
                    atomicEvidenceType = BoundedStringSchema(2, 180),
                    atomicEvidenceMode = new
                    {
                        type = "string",
                        @enum = new[] { "named_item", "content_claim" }
                    },
                    selectionPolicy = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "single_item", "explicit_set", "open_set",
                            "structured_layout"
                        }
                    },
                    initialCapability = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "documents_content_cards",
                            "documents_navigation",
                            "rag_search"
                        }
                    },
                    initialQuery = BoundedStringSchema(0, 160),
                    initialScopeId = new
                    {
                        type = "integer",
                        @enum = Enumerable.Range(
                                0,
                                SourceBackedRetrievalScope
                                    .GetAvailableCategoryPaths(intake)
                                    .Count + 1)
                            .ToArray()
                    },
                    initialLimit = new
                    {
                        type = "integer",
                        minimum = 1,
                        maximum = 60
                    },
                    structuredLayout = new { type = "boolean" },
                    rowCount = new
                    {
                        type = "integer",
                        minimum = 1,
                        maximum = 80
                    },
                    columnCount = new
                    {
                        type = "integer",
                        minimum = 1,
                        maximum = 40
                    },
                    atomicEvidenceCount = new
                    {
                        type = "integer",
                        minimum = 1,
                        maximum = 80
                    },
                    rowHeader = BoundedStringSchema(0, 60),
                    rowLabels = new
                    {
                        type = "array",
                        items = BoundedStringSchema(1, 60),
                        minItems = 0,
                        maxItems = 80
                    },
                    columns = new
                    {
                        type = "array",
                        items = BoundedStringSchema(1, 60),
                        minItems = 0,
                        maxItems = 40
                    }
                },
                required = new[]
                {
                    "planKind",
                    "deliverable",
                    "atomicEvidenceType",
                    "atomicEvidenceMode",
                    "selectionPolicy",
                    "initialCapability",
                    "initialQuery",
                    "initialScopeId",
                    "initialLimit"
                },
                additionalProperties = false
            }, ClientJson.CamelCase));

    private static SourceBackedInitialToolCall?
        TryBuildSemanticPlanInitialToolCall(
            SourceBackedIntake intake,
            SourceBackedAgentCompletion completion,
            string decisionSource)
    {
        if (completion.ToolCalls.Count != 1)
            return null;

        var arguments = completion.ToolCalls[0].Arguments;
        var initialCapability = GetString(
            arguments,
            "initialCapability")?.Trim();
        var initialQuery = GetString(arguments, "initialQuery")?.Trim()
                           ?? string.Empty;
        if (!TryGetInteger(arguments, "initialScopeId", out var scopeId)
            || !SourceBackedRetrievalScope.TryResolveCategoryScopeId(
                intake,
                scopeId,
                out var categoryPath)
            || !TryGetInteger(arguments, "initialLimit", out var initialLimit)
            || initialLimit is < 1 or > 60)
        {
            return null;
        }

        var toolName = initialCapability switch
        {
            "documents_content_cards" => "documents_content_cards",
            "documents_navigation" => "documents_navigation",
            "rag_search" when initialQuery.Length > 0 => "rag_search",
            _ => null
        };
        if (toolName is null)
            return null;

        var toolArguments = toolName switch
        {
            "documents_content_cards" => JsonSerializer.SerializeToElement(
                new
                {
                    categoryPath,
                    q = initialQuery.Length > 0 ? initialQuery : null,
                    limit = initialLimit,
                    offset = 0
                },
                ClientJson.CamelCase),
            "documents_navigation" => JsonSerializer.SerializeToElement(
                new
                {
                    categoryPath,
                    q = initialQuery.Length > 0 ? initialQuery : null,
                    limit = initialLimit,
                    offset = 0
                },
                ClientJson.CamelCase),
            _ => JsonSerializer.SerializeToElement(
                new
                {
                    query = initialQuery,
                    categoryPath,
                    topK = Math.Min(initialLimit, 20)
                },
                ClientJson.CamelCase)
        };
        return new SourceBackedInitialToolCall(
            "semantic-plan-initial-action",
            toolName,
            toolArguments,
            decisionSource);
    }

    private static object BoundedStringSchema(int minimumLength, int maximumLength)
        => new
        {
            type = "string",
            minLength = minimumLength,
            maxLength = maximumLength
        };

}
