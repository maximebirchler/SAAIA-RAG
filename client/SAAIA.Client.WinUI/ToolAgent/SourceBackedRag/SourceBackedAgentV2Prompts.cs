using System.Globalization;
using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string BuildSemanticPlanningPrompt()
        => """
           Tu planifies la mission d'un agent RAG local sans repondre ni utiliser d'outil.
           Produis exactement huit lignes non vides. Toutes sauf PREMIERE_ACTION ont
           22 mots maximum:
           LIVRABLE:
           DIMENSIONS:
           PREUVES_ATOMIQUES:
           INTENTIONS_RECHERCHE:
           APPROCHE_OUTILS:
           PREMIERE_ACTION:
           ACCEPTER_SI:
           INSUFFISANT_SEULEMENT_SI:

           Regles:
           - copie les exigences et axes explicites; n'en ajoute aucun;
           - INTERDIT sauf demande explicite: toute qualite, preference, sous-champ,
             methode, detail, etape ou quantite absente de la demande;
           - une preuve atomique est une instance complete, jamais sous-composant, rubrique,
             categorie, conseil, libelle d'axe ou case;
           - instance complete signifie une valeur utilisable du champ demande, pas une
             fiche detaillee; n'ajoute aucun sous-champ absent de la demande;
           - DIMENSIONS utilise exactement "N x M; lignes: ...; colonnes: ..." lorsqu'une
             grille visible est demandee, avec les nombres et libelles explicites dans leur
             ordre final; sinon ecris "aucune grille";
           - une plage finie explicite compte toutes ses positions visibles: ecris leur
             nombre et leurs libelles, sans la reduire a un seul livrable;
           - PREUVES_ATOMIQUES indique le nombre et le type d'instances attendues, jamais
             un corpus, une categorie, un document, un outil ou un budget de candidats;
             pour une grille, son nombre est exactement N multiplie par M;
           - la source prouve l'instance; l'orchestrateur choisit sa place;
           - une grille exige autant de cases utiles que le produit de ses axes;
           - les intentions sont des termes source compacts, sans jours ni simple mise en
             page; conserve les contraintes explicites qui changent vraiment la nature ou
             l'usage du contenu recherche;
           - APPROCHE_OUTILS choisit librement les capacites adaptees, sans nombre ni ordre
             impose et nomme les outils exacts choisis;
           - PREMIERE_ACTION contient ta premiere decision executable sous la forme exacte
             outil_externe {"argument":"valeur"}, ou aucune si aucun outil n'est encore utile;
           - lorsque les noms recherches sont inconnus et que tu choisis un inventaire,
             n'invente pas de filtre lexical a partir d'un axe purement visuel: omets q tant
             qu'aucun terme source ou sous-type semantiquement distinctif n'est connu;
           - q filtre litteralement l'inventaire: les mots generiques de la mission ou de la
             mise en forme sont des meta-termes, jamais des filtres distinctifs;
           - limit est un budget de candidats, pas un nombre d'instances automatiquement
             valides: choisis toi-meme une marge permettant d'ecarter les titres inadaptes,
             ou prevois de poursuivre avec nextOffset;
           - ne declare une insuffisance qu'apres navigation ou recherche pertinente;
           - INSUFFISANT_SEULEMENT_SI decrit les instances utiles encore introuvables,
             jamais l'absence d'une source classee pour un jour ou une case.

           Capacites et couts a comparer:
           - documents_content_cards parcourt directement beaucoup d'elements nommes, chacun
             deja citable par fichier/page; il permet de decouvrir des noms encore inconnus
             sans devoir deviner une requete lexicale et peut eviter de nombreuses recherches;
           - documents_navigation cartographie documents, sommaires et pages mais ne fournit
             aucune preuve finale; son resultat exige souvent un autre outil;
           - rag_search cible un contenu source precis et peut regrouper plusieurs intentions;
           - documents_context lit un passage deja localise.
           Choisis la strategie qui obtient les preuves necessaires avec le moins
           d'allers-retours utiles, sans sacrifier leur adequation.

           Arguments disponibles pour PREMIERE_ACTION:
           - documents_content_cards: categoryPath, categoryRef, docRef, docId, docPath,
             q optionnel, limit, offset;
           - documents_navigation: path, categoryPath, docRef, docPath, q, kind, limit, offset;
           - rag_search: query ou queries, topK, categoryPath, docId, docPath, pageStart,
             pageEnd,
             maxPerDoc, maxPerPage, diversity, mode;
           - documents_context: docRef, docId, docPath, chunkId, pageStart, pageEnd, before,
             after, limit, offset.
           Choisis toi-meme les arguments et la quantite utile. Le JSON doit etre valide,
           sur une seule ligne, sans Markdown.
           Exemple de forme pour decouvrir largement des noms inconnus, a adapter librement:
           documents_content_cards {"categoryPath":"CategorieConnue","limit":40,"offset":0}

           Avant de produire les huit lignes, verifie silencieusement que:
           - DIMENSIONS preserve chaque axe visible et PREUVES_ATOMIQUES vaut leur produit;
           - INTENTIONS_RECHERCHE ne contient aucun jour, semaine, ligne ou case;
           - APPROCHE_OUTILS tient compte du nombre d'instances et du caractere citable;
           - PREMIERE_ACTION applique reellement cette approche sans transformer les axes
             de presentation en filtres documentaires;
           - aucun detail absent de la demande n'a ete ajoute.
           """;

    private static string BuildPlanExecutionMessage(string semanticPlan)
        => $"""
            PLAN DE MISSION PRODUIT PAR LE LLM:
            {semanticPlan}

            Compare d'abord ce plan a la demande originale et retire mentalement tout detail
            ajoute. Ce plan est ta strategie courante: suis l'APPROCHE_OUTILS que tu as choisie
            tant qu'aucune observation nouvelle ne justifie de la changer. Tu restes libre
            de l'adapter selon les preuves observees, mais ne l'abandonne pas avant d'avoir
            essaye sa capacite pertinente. Recherche les composants documentaires, pas
            seulement une formulation litterale du livrable final.
            """;

    private static string BuildActionDecisionMessage(
        int? requiredAtomicEvidenceCount,
        SemanticLayoutDimensions? layoutDimensions,
        bool requireEvidenceSelection,
        bool enableWorkspaceWriterHandoff)
    {
        var selectionContract = enableWorkspaceWriterHandoff
            ? $"""

               Ton propre plan exige {requiredAtomicEvidenceCount!.Value} instances atomiques.
               Tant que l'outil manage_evidence_workspace n'est pas expose, poursuis
               librement avec la capacite documentaire utile. Lorsqu'assez de sources
               visibles distinctes existent, cet outil devient disponible: appelle-le avec
               status=ready_to_write et exactement {requiredAtomicEvidenceCount.Value}
               EvidenceId distincts dans retainEvidenceIds. Cet appel est ta decision finale:
               le redacteur utilisera uniquement cette selection. Si les preuves ne sont pas
               encore semantiquement suffisantes, n'appelle pas cet outil et poursuis les
               recherches avec une capacite documentaire.
               {(layoutDimensions is not null ? "Pour le livrable structure, ordonne retainEvidenceIds selon les cellules du plan LLM, ligne par ligne; le layout canonique sera reutilise mecaniquement." : string.Empty)}
               Avec ready_to_write, fournis uniquement la selection finale dans
               retainEvidenceIds; les candidats absents ne sont ni retenus ni rejetes.
               """
            : requiredAtomicEvidenceCount is > 1 || (
                    requireEvidenceSelection
                    && requiredAtomicEvidenceCount is > 0)
            ? $"""

               Ton propre plan exige {requiredAtomicEvidenceCount.Value} instances atomiques.
               Quand les preuves suffisent, appelle submit_evidence_selection avec exactement
               {requiredAtomicEvidenceCount.Value} EvidenceId distincts que tu juges
               semantiquement adaptes. Cet appel est ta decision finale de selection;
               le redacteur la mettra en forme sans choisir d'autres preuves. Ne renvoie pas
               PRET_A_REDIGER en texte.
               evidenceIds contient tous les identifiants retenus.
               Pour un livrable structure, leur ordre suit les cellules et layout contient
               uniquement les libelles de lignes et colonnes demandes.
               """
            : string.Empty;
        var readyAction = enableWorkspaceWriterHandoff
            ? """
              - Si manage_evidence_workspace est expose et si les preuves suffisent, appelle-le
                uniquement avec status=ready_to_write. Sinon, choisis un outil documentaire.
              """
            : requireEvidenceSelection
            ? """
              - Si les preuves suffisent, appelle submit_evidence_selection comme seule action.
                Le redacteur recevra ensuite uniquement ta selection autorisee.
              """
            : """
              - Si les preuves suffisent, n'appelle aucun outil et reponds seulement:
                PRET_A_REDIGER: suivi d'une note de 40 mots maximum sur le livrable.
              """;
        return """
               DECISION D'ORCHESTRATION UNIQUEMENT:
               Evalue les preuves deja observees et ce qui manque encore.
               - Si une action documentaire est utile, appelle librement le ou les outils adaptes.
               - Si seul l'utilisateur peut trancher une ambiguite materielle revelee par
                 les observations, appelle request_user_clarification comme unique action.
               """
               + Environment.NewLine
               + readyAction
               + """

               Ne redige pas encore la reponse finale, aucun tableau et aucune longue analyse.
               """
               + selectionContract;
    }

    private static string BuildDedicatedWriterMessage(string? actionDecision)
    {
        var decisionNote = string.IsNullOrWhiteSpace(actionDecision)
            ? string.Empty
            : Environment.NewLine
              + "NOTE DE LA DECISION D'ORCHESTRATION (indication seulement):"
              + Environment.NewLine
              + TrimPromptValue(actionDecision, 500);
        return """
               REDACTION FINALE SEPAREE:
               Produis maintenant uniquement le livrable final destine a l'utilisateur.
               Respecte exactement la demande, couvre toutes ses dimensions, utilise seulement
               les preuves visibles et place [E#] apres chaque fait ou cellule sourcee.
               Pour une grille, utilise un tableau concis. Aucun raisonnement, aucune
               auto-evaluation, aucune mention du plan, des outils, du juge ou de cette consigne.
               Si la note d'orchestration contient SELECTION, utilise exactement ces EvidenceId,
               une seule fois chacun, et aucun autre: cette selection est la decision semantique
               de l'orchestrateur. Associe chaque titre visible a une cellule plausible.
               """
               + decisionNote;
    }

    private static string BuildDedicatedWriterSystemPrompt()
        => """
           Tu es le redacteur final source-backed. Redige uniquement la reponse directement
           utile a l'utilisateur, dans sa langue. Respecte exactement la demande et sa forme.
           Utilise seulement les preuves E visibles dans l'etat de travail; place [E#]
           immediatement apres chaque fait ou cellule sourcee. N'invente aucun fait, nom,
           sous-composant, detail ou identifiant. Les lignes, colonnes et affectations d'une grille
           sont un choix de presentation et n'ont pas besoin d'etre presents dans la source.
           Quand l'utilisateur demande des instances nommees, ecarte rubriques, categories,
           sous-composants, instructions, OCR incoherent et libelles generiques. N'utilise pas
           deux fois le meme nom sous des EvidenceId differents. Affecte les instances aux
           colonnes de facon linguistiquement plausible et professionnelle, sans pretendre
           que cette affectation vient de la source.
           N'explique ni le raisonnement, ni les outils, ni le plan, ni les controles.
           """;

    private static string BuildCompactActionSystemPrompt(bool constrainedContext)
    {
        if (constrainedContext)
        {
            return """
                   Tu es l'orchestrateur semantique du RAG. Juge la demande et les preuves,
                   puis choisis librement une autre capacite ou la selection finale. Navigation
                   et memoire orientent sans prouver. Evalue les qualites par leur sens, sans
                   adjectif obligatoire ni proxy invente. Ecarte rubriques, composants, OCR
                   incoherent et doublons. Une selection exige une preuve E complete et adaptee
                   par instance. Plusieurs EvidenceId du meme groupe_source ne constituent
                   qu'une source visible. Compare aussi le rendement des capacites en groupes
                   distincts: pour beaucoup d'elements nommes inconnus, un inventaire de cartes
                   peut etre plus efficace que plusieurs extraits issus des memes pages. Tu
                   peux demander plusieurs appels documentaires independants dans un meme tour;
                   ils seront executes en parallele. Tu gardes le choix de l'outil. Le code ne
                   controle que forme, identifiants et citations.
                   """;
        }

        return """
               Tu restes l'orchestrateur semantique principal du RAG. Compare la demande originale,
               le plan LLM et l'etat compact, puis choisis librement les outils encore utiles.
               Evalue les qualites explicites semantiquement: ne transforme jamais simple,
               facile, accessible, technique ou economique en seuil quantitatif non demande,
               et n'exige pas que la preuve repete exactement le meme adjectif.
               Les descriptions natives exposent leurs capacites et leurs arguments. Une navigation
               est un pointeur non citable; la memoire aide a comprendre mais ne prouve rien.
               Pour une cible lexicale, utilise des termes susceptibles d'apparaitre dans la source,
               jamais une meta-requete. Pour beaucoup de noms inconnus, compare recherche et
               inventaire citable. Evite les actions identiques. Ne declare la collecte terminee
               que si toutes les instances explicites sont couvertes par des EvidenceId visibles.
               Le nombre de candidats visibles ne prouve pas leur adequation: ecarte titres de
               section, categories, sous-composants, instructions, OCR incoherent et doublons avant
               de conclure que le nombre d'instances utiles est atteint.
               Un axe de grille peut aussi exprimer une vraie contrainte d'usage. Dans ce cas,
               evalue cette adequation et recherche un sous-type pertinent si les candidats
               visibles ne permettent pas une affectation professionnelle; ne remplis jamais
               une case uniquement pour atteindre le nombre demande.
               manage_evidence_workspace est facultatif: il preserve les EvidenceId que tu
               retiens et masque ceux que tu refuses pendant les compactages suivants. Tu
               peux l'appeler seul ou avec une action documentaire.
               Tu peux demander plusieurs appels documentaires complementaires et independants
               dans un meme tour; ils seront executes en parallele.
               Le code verifie les contrats mecaniques; tu gardes toute decision semantique.
               """;
    }

    private IReadOnlyList<SourceBackedAgentMessage> BuildSemanticSelectionDecisionMessages(
        SourceBackedIntake intake,
        string semanticPlan,
        EvidenceBundle bundle,
        IReadOnlyList<string> selectableEvidenceIds,
        string? semanticReviewFeedback,
        int requiredCount,
        SemanticLayoutDimensions? dimensions,
        string? canonicalRowHeader,
        IReadOnlyList<string>? canonicalRowLabels,
        IReadOnlyList<string>? canonicalColumnLabels,
        bool canonicalLayoutAlreadyDefined,
        bool useWorkspaceWriterHandoff)
    {
        var constrainedContext = _options.MaximumContextTokens <= 4096;
        var context = new StringBuilder();
        context.AppendLine("CONTEXTE COMPACT DE SELECTION FINALE");
        context.Append("DEMANDE: ")
            .AppendLine(TrimPromptValue(
                intake.UserQuestion,
                constrainedContext ? 420 : 600));
        context.Append("PLAN LLM: ")
            .AppendLine(TrimPromptValue(
                semanticPlan,
                constrainedContext ? 520 : 700));
        AppendQuestionFocusContext(context, intake);
        context.Append("CONTRAT: exactement ")
            .Append(requiredCount)
            .Append(" EvidenceId distincts");
        if (dimensions is not null)
        {
            context.Append(" en ")
                .Append(dimensions.Rows)
                .Append(" lignes x ")
                .Append(dimensions.Columns)
                .Append(" colonnes");
        }
        context.AppendLine(".");
        if (dimensions is not null
            && canonicalRowLabels is { Count: > 0 }
            && canonicalColumnLabels is { Count: > 0 })
        {
            context.AppendLine("ORDRE CANONIQUE DECIDE PAR LE LLM:");
            if (!string.IsNullOrWhiteSpace(canonicalRowHeader))
            {
                context.Append("- en-tete_lignes: ")
                    .AppendLine(TrimPromptValue(canonicalRowHeader, 60));
            }
            context.Append("- lignes, dans l'ordre: ")
                .AppendLine(string.Join(
                    " | ",
                    canonicalRowLabels.Select(static label =>
                        TrimPromptValue(label, 80))));
            context.Append("- colonnes, dans l'ordre: ")
                .AppendLine(string.Join(
                    " | ",
                    canonicalColumnLabels.Select(static label =>
                        TrimPromptValue(label, 60))));
            context.AppendLine(
                "La premiere preuve correspond a la premiere colonne de la premiere ligne; poursuis ligne par ligne.");
        }
        if (!string.IsNullOrWhiteSpace(semanticReviewFeedback))
        {
            context.AppendLine("DERNIER RETOUR A CORRIGER:");
            context.AppendLine(TrimPromptValue(
                semanticReviewFeedback,
                constrainedContext ? 650 : 1200));
        }
        AppendSemanticColumnRoles(
            context,
            intake.CanonicalColumnSemanticRoles);
        context.AppendLine(
            "CANDIDATS CITABLES, SANS ORDRE DE PREFERENCE; chaque ligne conserve son fichier, sa page et son groupe_source mecanique:");
        var visibleSourceGroups = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var nextVisibleSourceGroup = 1;
        foreach (var evidenceId in selectableEvidenceIds)
        {
            if (!bundle.ById.TryGetValue(evidenceId, out var item))
                continue;

            if (!visibleSourceGroups.TryGetValue(
                    item.VisibleSourceKey,
                    out var visibleSourceGroup))
            {
                visibleSourceGroup = "V"
                                     + nextVisibleSourceGroup.ToString(
                                         "D3",
                                         CultureInfo.InvariantCulture);
                visibleSourceGroups[item.VisibleSourceKey] = visibleSourceGroup;
                nextVisibleSourceGroup++;
            }

            context.Append("- ")
                .Append(evidenceId)
                .Append(" | groupe_source=")
                .Append(visibleSourceGroup)
                .Append(" | valeur=")
                .Append(TrimPromptValue(
                    GetEvidenceDisplayValue(item),
                    constrainedContext ? 100 : 130));
            if (constrainedContext)
            {
                context.Append(" | type=")
                    .Append(item.SelectionHints.TryGetValue("kind", out var kind)
                        ? TrimPromptValue(kind, 28)
                        : item.SourceKind);
                AppendEvidenceHeadingPath(
                    context,
                    item,
                    maximumCharacters: 80);
                context
                    .Append(" | preuve=")
                    .Append(TrimPromptValue(item.Excerpt, 72))
                    .Append(" | source=")
                    .Append(TrimPromptValue(
                        BuildEvidenceDocumentMetadata(item),
                        180))
                    .AppendLine();
            }
            else
            {
                context
                    .Append(" | type_source=")
                    .Append(item.SelectionHints.TryGetValue("kind", out var kind)
                        ? TrimPromptValue(kind, 40)
                        : item.SourceKind);
                AppendEvidenceHeadingPath(
                    context,
                    item,
                    maximumCharacters: 120);
                context
                    .Append(" | preuve_structuree=")
                    .Append(item.SelectionHints.TryGetValue("hasGroundedEvidence", out var grounded)
                        ? TrimPromptValue(grounded, 10)
                        : "inconnu")
                    .Append(" | fichier=")
                    .Append(TrimPromptValue(item.DocName ?? item.DocPath ?? item.DocId, 90))
                    .Append(" | p.")
                    .AppendLine(item.PageStart?.ToString(CultureInfo.InvariantCulture) ?? "?");
            }
        }
        context.AppendLine(
            useWorkspaceWriterHandoff
                ? dimensions is null
                    ? "Choisis semantiquement les preuves qui satisfont la demande. Appelle uniquement manage_evidence_workspace avec status=ready_to_write, exactement les EvidenceId choisis dans retainEvidenceIds et une note de decision breve."
                    : "Choisis semantiquement les preuves qui satisfont la demande et ordonne retainEvidenceIds selon les cellules du plan LLM, ligne par ligne. Appelle uniquement manage_evidence_workspace avec status=ready_to_write; le layout canonique sera reutilise mecaniquement."
                : dimensions is null
                    ? "Choisis semantiquement les preuves qui satisfont la demande. Appelle uniquement submit_evidence_selection avec evidenceIds; aucun layout n'est attendu."
                    : canonicalLayoutAlreadyDefined
                        ? "Choisis et ordonne semantiquement les valeurs dans l'ordre des cellules du plan LLM. Appelle uniquement submit_evidence_selection avec evidenceIds; le layout canonique deja decide par le LLM sera reutilise mecaniquement."
                        : "Choisis semantiquement les valeurs qui satisfont la demande et le dernier verdict. Appelle uniquement submit_evidence_selection.");

        return new[]
        {
            SourceBackedAgentMessage.System(
                useWorkspaceWriterHandoff
                    ? dimensions is null
                        ? $"""
                       Tu es l'orchestrateur semantique principal. Ce tour sert uniquement
                       a transmettre au redacteur les preuves que tu as jugees suffisantes
                       et adaptees a la demande. Le code verifie seulement cardinalite,
                       identifiants et unicite de source visible. Choisis au maximum un
                       EvidenceId par groupe_source. Appelle exactement
                       manage_evidence_workspace avec status=ready_to_write, exactement
                       {requiredCount} identifiants dans retainEvidenceIds et une note breve.
                       N'appelle aucun autre outil et ne produis aucun texte de reponse.
                       """
                        : $"""
                       Tu es l'orchestrateur semantique principal. Ce tour sert uniquement
                       a transmettre ta selection finale pour le layout canonique deja
                       decide. Le code verifie seulement cardinalite, identifiants et
                       unicite de source visible. Choisis au maximum un EvidenceId par
                       groupe_source et ordonne exactement {requiredCount} identifiants
                       dans retainEvidenceIds selon les cellules du plan LLM, ligne par
                       ligne. Appelle exactement manage_evidence_workspace avec
                       status=ready_to_write et une note breve. N'appelle aucun autre outil
                       et ne produis aucun texte de reponse.
                       """
                    : dimensions is null
                    ? """
                      Tu es l'orchestrateur semantique principal. Ce tour sert uniquement
                      a transmettre au redacteur les preuves que tu as jugees suffisantes
                      et adaptees a la demande. Le code verifie seulement cardinalite,
                      identifiants et unicite de source visible. Choisis au maximum un
                      EvidenceId par groupe_source. Appelle exactement
                      submit_evidence_selection avec evidenceIds, sans layout, sans texte
                      ni autre outil.
                      """
                    : canonicalLayoutAlreadyDefined
                    ? """
                      Tu es l'orchestrateur semantique principal. Choisis au maximum un
                      EvidenceId par groupe_source et ordonne evidenceIds selon les cellules
                      du plan LLM, ligne par ligne. Le layout canonique a deja ete decide par
                      le LLM et sera reutilise mecaniquement: ne le repete pas. Le code controle
                      seulement cardinalite, identifiants, unicite de source visible et forme.
                      Appelle exactement submit_evidence_selection avec evidenceIds, sans
                      layout, sans texte ni autre outil.
                      """
                    : constrainedContext
                    ? """
                      Tu es l'orchestrateur semantique principal. Ce tour sert uniquement
                      a choisir et ordonner les preuves affichees selon la demande. Le code
                      controle seulement cardinalite, identifiants, unicite de source visible
                      et forme. Choisis au maximum un EvidenceId par groupe_source.
                      Ecarte rubriques, composants, instructions, OCR incoherent, libelles
                      generiques et doublons. Controle chaque correspondance ligne/colonne.
                      evidenceIds suit l'ordre des cellules; layout contient seulement des
                      libelles texte distincts. Appelle exactement
                      submit_evidence_selection, sans texte ni autre outil.
                      """
                    : """
                      Tu restes l'orchestrateur semantique principal. Ce tour est uniquement
                      une decision de selection parmi les candidats affiches. Le code ne choisit
                      aucune valeur: il verifie seulement cardinalite, identifiants, unicite et
                      forme. Chaque groupe_source represente une meme source visible: choisis au
                      maximum un EvidenceId par groupe_source. Ecarte toi-meme rubriques,
                      sous-composants, instructions, OCR incoherent, libelles generiques et
                      doublons semantiques.

                      evidenceIds est l'unique liste d'identifiants, dans l'ordre des cellules.
                      Cet ordre est une decision semantique: controle chaque correspondance entre
                      la valeur et son libelle de ligne/colonne. Une instance valide ne devient
                      pas automatiquement une affectation professionnelle. Si les candidats
                      affiches ne permettent pas de satisfaire un axe, ne simule pas sa couverture.
                      Verifie qu'aucun identifiant n'apparait deux fois. layout.rowHeader est un
                      texte; layout.columns et layout.rows contiennent uniquement des libelles
                      texte distincts, jamais des objets ni des EvidenceId. Appelle exactement
                      submit_evidence_selection, sans texte ni autre outil.
                      """),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
    }

    private static void AppendSemanticColumnRoles(
        StringBuilder builder,
        IReadOnlyDictionary<string, string>? roles)
    {
        if (roles is not { Count: > 0 })
            return;

        builder.AppendLine(
            "ROLES SEMANTIQUES DES COLONNES DECIDES PAR LE LLM:");
        foreach (var role in roles)
        {
            builder
                .Append("- ")
                .Append(TrimPromptValue(role.Key, 60))
                .Append(": ")
                .AppendLine(TrimPromptValue(role.Value, 180));
        }
    }

    private static string BuildSemanticRevisionNoProgressFeedback(SemanticReview review)
        => $"""
            REVISION SANS PROGRES DETECTEE MECANIQUEMENT:
            le dernier brouillon est textuellement identique au brouillon deja refuse.
            Le juge maintient les motifs suivants:
            {string.Join(Environment.NewLine, review.Reasons.Select(static reason => "- " + reason))}

            La repetition du meme texte ne peut pas corriger ces motifs. Reprends une decision
            d'orchestration avec les outils disponibles: cherche librement des preuves
            complementaires si elles manquent, ou choisis une strategie reellement differente
            fondee sur les preuves visibles. Ne redige pas encore le meme brouillon.
            """;

    private static string BuildUserContext(
        SourceBackedIntake intake,
        int maximumContextTokens)
    {
        var builder = new StringBuilder();
        builder.AppendLine("DEMANDE UTILISATEUR:");
        builder.AppendLine(intake.UserQuestion.Trim());
        builder.AppendLine();
        builder.AppendLine("LANGUE DE REPONSE: " + intake.Language);
        AppendQuestionFocusContext(builder, intake);
        AppendNamedDocumentResolutionContext(builder, intake);
        AppendSemanticColumnRoles(
            builder,
            intake.CanonicalColumnSemanticRoles);
        if (intake.CatalogHints is { Count: > 0 })
        {
            builder.AppendLine("CATALOGUE DISPONIBLE (orientation, pas preuve):");
            foreach (var hint in intake.CatalogHints.Take(24))
            {
                builder.Append("- categoryPath=")
                    .Append('"')
                    .Append(hint.CategoryPath)
                    .Append('"');
                if (!string.Equals(
                        hint.DisplayName,
                        hint.CategoryPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append(" | displayName=")
                        .Append('"')
                        .Append(hint.DisplayName)
                        .Append('"');
                }
                if (hint.Aliases.Count > 0)
                    builder.Append(" | alias: ").Append(string.Join(", ", hint.Aliases.Take(4)));
                builder.AppendLine();
            }
        }

        AppendMemoryContext(
            builder,
            intake.MemoryContext,
            maximumContextTokens);
        return builder.ToString().Trim();
    }

    private static string BuildWorkingUserContext(
        SourceBackedIntake intake,
        int maximumContextTokens,
        bool emergencyContextRecovery)
    {
        var builder = new StringBuilder();
        builder.Append("DEMANDE: ")
            .AppendLine(TrimPromptValue(
                intake.UserQuestion,
                emergencyContextRecovery ? 600 : 900));
        builder.AppendLine("LANGUE: " + intake.Language);
        AppendQuestionFocusContext(builder, intake);
        AppendNamedDocumentResolutionContext(builder, intake);
        AppendSemanticColumnRoles(
            builder,
            intake.CanonicalColumnSemanticRoles);
        AppendMemoryContext(
            builder,
            intake.MemoryContext,
            maximumContextTokens,
            emergencyContextRecovery ? 480 : null);
        return builder.ToString().Trim();
    }

    private static void AppendNamedDocumentResolutionContext(
        StringBuilder builder,
        SourceBackedIntake intake)
    {
        var observation = intake.RequestedDocumentResolution;
        if (observation is null)
            return;
        builder.AppendLine("RESOLUTION DU DOCUMENT NOMME (catalogue, jamais preuve de contenu):");
        builder.Append("- reference=")
            .AppendLine(observation.RequestedReference);
        builder.Append("- status=")
            .Append(observation.Status.ToString().ToLowerInvariant())
            .Append(" | complete=")
            .Append(observation.CatalogObservationComplete ? "true" : "false")
            .Append(" | reason=")
            .AppendLine(observation.ReasonCode);
        builder.Append("- documentScope=")
            .AppendLine(intake.DocumentScope.ToString().ToLowerInvariant());
        foreach (var candidate in observation.Candidates.Take(8))
        {
            builder.Append("- candidate docId=")
                .Append(candidate.DocId)
                .Append(" | docPath=")
                .AppendLine(candidate.DocPath);
        }
        if (intake.DocumentScope == SourceBackedDocumentScope.AlternativeSources
            && !string.IsNullOrWhiteSpace(intake.DocumentScopeDisclosure))
        {
            builder.AppendLine(
                "DIVULGATION DE PORTEE A REPRENDRE EXACTEMENT DANS LA REPONSE:");
            builder.AppendLine(intake.DocumentScopeDisclosure.Trim());
        }
    }

}
