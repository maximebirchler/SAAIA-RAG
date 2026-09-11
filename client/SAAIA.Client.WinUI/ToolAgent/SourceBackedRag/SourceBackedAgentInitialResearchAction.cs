using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record InitialResearchActionOutcome(
        IReadOnlyList<SourceBackedInitialToolCall> Actions,
        bool Attempted,
        bool ProtocolValid,
        int Attempts,
        int PromptTokens,
        int CompletionTokens,
        string FailureReason,
        string CoverageBudget);

    private async Task<InitialResearchActionOutcome>
        PrepareMissingInitialResearchActionAsync(
            SourceBackedIntake intake,
            SemanticPlanPreparation semanticPlanPreparation,
            string semanticPlan,
            SemanticLayoutDimensions? layoutDimensions,
            SemanticCandidateDefinitionOutcome candidateDefinition,
            SemanticCandidateStrategyOutcome candidateStrategy,
            CandidateProjectedEvidenceContractOutcome projectedContract,
            IReadOnlyDictionary<string, string> columnRoles,
            IReadOnlyList<SourceBackedAgentToolDefinition> availableTools,
            bool suppressForNamedDocumentQuarantine,
            CancellationToken ct)
    {
        if (suppressForNamedDocumentQuarantine)
        {
            return new InitialResearchActionOutcome(
                Array.Empty<SourceBackedInitialToolCall>(),
                Attempted: false,
                ProtocolValid: true,
                Attempts: 0,
                PromptTokens: 0,
                CompletionTokens: 0,
                FailureReason: "named_document_initial_action_quarantined",
                CoverageBudget: string.Empty);
        }

        var alreadyHasAction =
            intake.InitialToolCalls is { Count: > 0 }
            || semanticPlanPreparation.InitialToolCall is not null;
        var comesFromRouterMission = string.Equals(
            semanticPlanPreparation.DecisionSource,
            "llm_router",
            StringComparison.OrdinalIgnoreCase);
        if (alreadyHasAction || !comesFromRouterMission)
        {
            return new InitialResearchActionOutcome(
                Array.Empty<SourceBackedInitialToolCall>(),
                Attempted: false,
                ProtocolValid: true,
                Attempts: 0,
                PromptTokens: 0,
                CompletionTokens: 0,
                FailureReason: string.Empty,
                CoverageBudget: string.Empty);
        }
        if (candidateStrategy.Attempted
            && candidateStrategy.InitialAction is { } candidateDiscoveryAction)
        {
            return new InitialResearchActionOutcome(
                new[] { candidateDiscoveryAction },
                Attempted: false,
                ProtocolValid: true,
                Attempts: 0,
                PromptTokens: 0,
                CompletionTokens: 0,
                FailureReason: string.Empty,
                CoverageBudget: string.Empty);
        }

        SourceBackedAgentCompletion? previousCompletion = null;
        var candidateScopePaths = candidateStrategy.CandidateScopePaths;
        var candidatePoolRelation = candidateStrategy.Attempted
            ? candidateStrategy.CandidatePoolRelation
            : PartitionedCandidatePoolRelation;
        var permittedCategoryPaths = ResolveInitialResearchCategoryPaths(
            intake,
            candidateScopePaths);
        var failureReason = string.Empty;
        var promptTokens = 0;
        var completionTokens = 0;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var usesCompactObservationDecision = !candidateStrategy.Attempted;
            var completion = await _llm.CompleteAsync(
                    BuildInitialResearchActionMessages(
                        intake,
                        semanticPlanPreparation,
                        semanticPlan,
                        layoutDimensions,
                        candidateDefinition,
                        candidateStrategy,
                        projectedContract,
                        columnRoles,
                        permittedCategoryPaths,
                        attempt,
                        failureReason,
                        previousCompletion),
                    usesCompactObservationDecision
                        ? BuildInitialObservationDecisionTools(
                            permittedCategoryPaths)
                        : BuildInitialResearchBatchTools(
                            _options.MaximumWorkingEvidenceItems,
                            permittedCategoryPaths,
                            candidatePoolRelation),
                    usesCompactObservationDecision
                        ? Math.Clamp(
                            _options.MaximumActionTokens,
                            128,
                            320)
                        : Math.Clamp(
                            _options.MaximumActionTokens,
                            256,
                            640),
                    ct,
                    temperatureOverride: 0,
                    requireToolCall: true)
                .ConfigureAwait(false);
            promptTokens += completion.PromptTokens.GetValueOrDefault();
            completionTokens += completion.CompletionTokens.GetValueOrDefault();

            IReadOnlyList<SourceBackedInitialToolCall> actions;
            var coverageBudget = string.Empty;
            bool protocolValid;
            if (usesCompactObservationDecision)
            {
                protocolValid = TryReadInitialObservationDecision(
                    completion,
                    intake,
                    availableTools,
                    _options.MaximumWorkingEvidenceItems,
                    ReadRequiredAtomicEvidenceCount(semanticPlan)
                        .GetValueOrDefault(1),
                    permittedCategoryPaths,
                    out actions,
                    out coverageBudget,
                    out failureReason);
            }
            else
            {
                protocolValid = TryReadInitialResearchActions(
                    completion,
                    intake,
                    availableTools,
                    _options.MaximumWorkingEvidenceItems,
                    candidatePoolRelation,
                    candidateScopePaths,
                    out actions,
                    out failureReason);
            }
            if (protocolValid)
            {
                return new InitialResearchActionOutcome(
                    actions,
                    Attempted: true,
                    ProtocolValid: true,
                    Attempts: attempt,
                    PromptTokens: promptTokens,
                    CompletionTokens: completionTokens,
                    FailureReason: failureReason,
                    CoverageBudget: coverageBudget);
            }

            previousCompletion = completion;
        }

        return new InitialResearchActionOutcome(
            Array.Empty<SourceBackedInitialToolCall>(),
            Attempted: true,
            ProtocolValid: false,
            Attempts: 2,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            FailureReason: failureReason,
            CoverageBudget: string.Empty);
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildInitialResearchActionMessages(
            SourceBackedIntake intake,
            SemanticPlanPreparation semanticPlanPreparation,
            string semanticPlan,
            SemanticLayoutDimensions? layoutDimensions,
            SemanticCandidateDefinitionOutcome candidateDefinition,
            SemanticCandidateStrategyOutcome candidateStrategy,
            CandidateProjectedEvidenceContractOutcome projectedContract,
            IReadOnlyDictionary<string, string> columnRoles,
            IReadOnlyList<string> permittedCategoryPaths,
            int attempt,
            string failureReason,
            SourceBackedAgentCompletion? previousCompletion)
    {
        if (!candidateStrategy.Attempted)
        {
            return BuildDirectInitialResearchActionMessages(
                intake,
                semanticPlan,
                candidateDefinition,
                permittedCategoryPaths,
                attempt,
                failureReason,
                previousCompletion);
        }

        var context = new StringBuilder();
        context.Append("DEMANDE_ORIGINALE: ")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 700));
        context.AppendLine("MISSION_DEJA_INTERPRETEE:");
        foreach (var line in semanticPlan
                     .Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries
                         | StringSplitOptions.TrimEntries)
                     .Where(static line =>
                         line.StartsWith(
                             "LIVRABLE:",
                             StringComparison.OrdinalIgnoreCase)
                         || line.StartsWith(
                             "PREUVES_ATOMIQUES:",
                             StringComparison.OrdinalIgnoreCase)))
        {
            context.AppendLine(TrimPromptValue(line, 260));
        }
        context.AppendLine(
            "AUCUNE_OBSERVATION_DOCUMENTAIRE_N_A_ENCORE_ETE_LUE.");
        if (layoutDimensions is not null)
        {
            context.Append("COORDONNEES_LIGNES: ")
                .Append(TrimPromptValue(
                    semanticPlanPreparation.RowHeader,
                    60))
                .Append(" = ")
                .AppendLine(string.Join(
                    " | ",
                    semanticPlanPreparation.RowLabels));
            context.Append("COORDONNEES_COLONNES: ")
                .AppendLine(string.Join(
                    " | ",
                    semanticPlanPreparation.ColumnRoles.Keys));
        }

        if (projectedContract.Applied)
        {
            context.Append("CONTRAT_DE_PREUVE_PROJETE: ")
                .AppendLine(TrimPromptValue(
                    projectedContract.EvidenceContract,
                    260));
        }
        context.Append("MODE_DE_DECOUVERTE_DECIDE_PAR_LE_LLM: ")
            .AppendLine(candidateStrategy.CandidatePoolRelation);
        context.Append("TYPE_D_OBJET_SOURCE_CIBLE_DECIDE_PAR_LE_LLM: ")
            .AppendLine(TrimPromptValue(
                candidateStrategy.CandidateObjectType,
                180));
        context.Append("CRITERE_D_ELIGIBILITE_DECIDE_PAR_LE_LLM: ")
            .AppendLine(TrimPromptValue(
                candidateStrategy.CandidateEligibilityRule,
                300));
        context.Append("REQUETE_SOURCE_CANDIDATE_DECIDEE_PAR_LE_LLM: ")
            .AppendLine(TrimPromptValue(
                candidateStrategy.SourceDiscoveryQuery,
                140));
        if (candidateStrategy.CandidateScopePaths.Count > 0)
        {
            context.Append("PERIMETRES_CHOISIS_PAR_LE_LLM: ")
                .AppendLine(string.Join(
                    " | ",
                    candidateStrategy.CandidateScopePaths.Select(static path =>
                        string.IsNullOrWhiteSpace(path)
                            ? "corpus complet"
                            : path)));
        }
        if (permittedCategoryPaths.Count > 0)
        {
            context.AppendLine("PERIMETRES_CATALOGUE_EXACTS:");
            foreach (var categoryPath in permittedCategoryPaths.Take(40))
            {
                context.Append("- ")
                    .AppendLine(TrimPromptValue(categoryPath, 120));
            }
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
            "Appelle uniquement submit_initial_research_batch, sans texte.");
        return new[]
        {
            SourceBackedAgentMessage.System(
                """
                Tu choisis le plus petit lot utile de premieres actions documentaires
                d'une mission deja interpretee. Ne reinterprete pas le livrable et
                ne reponds pas.

                Les axes affiches sont les coordonnees du livrable final. Un libelle
                d'axe n'est un terme de source que s'il decrit reellement le contenu
                a retrouver. Ne cherche jamais un libelle qui sert seulement a
                repartir, ordonner ou presenter les candidats. Ne cherche pas le
                nom, la periode, la structure ou les dimensions du livrable final.
                Oriente plutot la decouverte vers le TYPE_D_OBJET_SOURCE_CIBLE et le
                CRITERE_D_ELIGIBILITE deja decides par le LLM. Ces informations
                definissent la cible semantique; elles ne constituent pas une liste
                de mots a recopier aveuglement dans query.
                REQUETE_SOURCE_CANDIDATE_DECIDEE_PAR_LE_LLM est une formulation
                lexicale source proposee lors de la strategie. Evalue-la comme point
                de depart prioritaire pour q lorsqu'elle correspond a la capacite
                choisie.
                Les axes servent d'abord a l'affectation finale. Un libelle d'axe ne
                devient une requete documentaire que lorsque tu juges qu'il decrit
                reellement le contenu source necessaire a la lacune courante.

                Respecte MODE_DE_DECOUVERTE_DECIDE_PAR_LE_LLM. Avec shared_pool, ne
                decoupe jamais le premier budget par ligne ou colonne: decouvre un vivier
                commun de la classe PREUVES_ATOMIQUES deja decidee lors de l'intake. Si les noms concrets sont
                inconnus et qu'un perimetre catalogue exact existe, compare une carte de
                navigation structurelle et un inventaire de cartes citables selon ce que le
                corpus peut exposer le plus economiquement. Garde assez de marge pour absorber
                les rejets. Avec
                partitioned_pool seulement, plusieurs actions par frontiere peuvent etre
                justifiees.

                Avec shared_pool, choisis sharedScope une seule fois au niveau du lot.
                Toutes les actions en heritent automatiquement: ne repete pas le scope
                dans chaque action.
                Si ce perimetre est corpus complet, tu peux le reduire a un unique scope
                exact affiche dans PERIMETRES_CATALOGUE_EXACTS: ce sous-perimetre reste
                compatible avec la strategie et devient alors le scope commun du lot.
                Une categorie supplementaire ne constitue pas
                de la diversite documentaire: elle contredit le vivier commun et doit attendre
                une observation qui justifie reellement un pivot.

                - documents_navigation explore rapidement titres, sommaires, index et
                  plages de pages. C'est un bon premier choix si cette structure peut
                  exposer beaucoup de noms inconnus avec un terme source compact. Les
                  pointeurs ne sont pas des preuves finales: selectionne ensuite les
                  ancres utiles et transforme-les en preuves par une recherche groupee ou
                  une lecture ciblee. navigationKind=\"navigation_entry\" demande les
                  entrees structurees de sommaire/index; \"title_anchor\" demande les
                  autres titres; une chaine vide conserve les deux. Avec beaucoup
                  d'instances attendues, dimensionne librement limit pour depasser les
                  pages liminaires et rendre plusieurs noms reellement visibles. Une
                  navigation non filtree avec une petite limite ne montre que le debut
                  ordonne du catalogue.
                - documents_content_cards decouvre directement de nombreux items
                  nommes et citables. Une carte peut toutefois representer une feuille
                  structurelle plutot que l'objet parent. Laisse query vide lorsqu'aucun
                  terme source fiable n'est encore connu; limit est un budget de
                  comparaison et la pagination reste possible. Le lot devra ensuite
                  survivre a un audit semantique: si plusieurs instances sont necessaires,
                  prevois librement une marge de candidats plutot que de confondre limit
                  avec le quota final. Si N instances finales sont requises et que des
                  rejets sont possibles, limit=N ne fournit mathematiquement aucune marge.
                - rag_search cible un terme, un fait ou un passage source precis deja
                  formulable depuis la demande ou la cible de preuve. N'ajoute aucun
                  adjectif, qualite, exemple, horaire, nom ou critere absent des
                  donnees affichees.
                - documents_context lit un document, une page ou une ancre deja
                  reellement identifies.

                Une seule action suffit si elle expose vraisemblablement la matiere
                necessaire. Utilise plusieurs actions seulement lorsque plusieurs
                intentions documentaires independantes peuvent etre lancees ensemble.
                Elles seront executees en parallele. Ne duplique pas la meme intention
                sous plusieurs formulations et ne prevois pas ici une action qui
                depend du resultat d'une autre.
                documents_context est interdit sans document, ancre ou page deja
                presents dans la demande. Ne l'ajoute jamais comme action exploratoire.

                Le budget global de ce lot est la limite maximale affichee par le schema:
                la somme de limit sur toutes les actions ne doit jamais la depasser. Ce
                plafond porte sur tout le lot, pas sur chaque action separement.

                La diversite des preuves n'impose pas une diversite de categories.
                Utilise le plus petit ensemble de scopes dont le libelle couvre
                directement la classe de preuve. Si un scope exact la couvre, garde
                les premieres actions dans ce scope sauf besoin transversal explicite;
                n'explore jamais une categorie sans rapport pour diversifier le lot.

                Une preuve candidate n'a pas a couvrir simultanement toutes les
                coordonnees. Choisis librement la capacite la plus economique qui
                expose des candidats citables sans inventer de filtre.

                scope est ta decision semantique de corpus: choisis le perimetre
                catalogue exact le plus etroit dont le libelle couvre reellement
                la cible de preuve et son critere d'eligibilite. Un scope dont le
                libelle nomme directement ce domaine domine la chaine vide. Une
                demande large ou plusieurs instances dans le meme domaine ne
                justifient pas le corpus complet. Utilise la chaine vide seulement
                si aucun perimetre affiche ne couvre fiablement ce domaine.
                document et anchor restent vides sans pointeur reel.
                """),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
    }

    private static IReadOnlyList<string> ResolveInitialResearchCategoryPaths(
        SourceBackedIntake intake,
        IReadOnlyList<string> candidateScopePaths)
    {
        var exactCandidateScopes = candidateScopePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return exactCandidateScopes.Length > 0
            ? exactCandidateScopes
            : SourceBackedRetrievalScope.GetAvailableCategoryPaths(intake);
    }

    private void AddInitialResearchActionTrace(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        InitialResearchActionOutcome outcome)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Planner,
            "source_backed_agent_v2.initial_research_action.completed",
            ("attempted", outcome.Attempted),
            ("protocol_valid", outcome.ProtocolValid),
            ("attempts", outcome.Attempts),
            ("prompt_tokens", outcome.PromptTokens),
            ("completion_tokens", outcome.CompletionTokens),
            ("decision_source", outcome.Actions.FirstOrDefault()?.DecisionSource),
            ("action_count", outcome.Actions.Count),
            ("tools", outcome.Actions
                .Select(static action => action.ToolName)
                .ToArray()),
            ("arguments", outcome.Actions
                .Select(static action => TrimPromptValue(
                    action.Arguments.GetRawText(),
                    800))
                .ToArray()),
            ("coverage_budget", outcome.CoverageBudget),
            ("failure_reason", outcome.FailureReason)));
}
