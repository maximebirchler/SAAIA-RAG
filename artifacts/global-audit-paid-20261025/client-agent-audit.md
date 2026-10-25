# Audit client et chaîne LLM — 25 octobre 2026

Audit **en lecture seule**, sur la base HEAD `1e6b7e5` annoncée propre par le responsable de campagne. Aucun ordre Git, build, test, appel réseau, modèle ou lecture de secret n’a été effectué. Seul ce rapport neuf est écrit ; les campagnes précédentes restent immuables. Les constats « prouvés » ci-dessous sont prouvés par le chemin statique du code, sauf mention explicite d’une preuve native antérieure. Les reproductions proposées ne sont pas exécutées dans cet audit.

La base ne peut pas être déclarée parfaite après la validation WinUI : celle-ci prouvait des comportements précis du client, pas la fidélité de toutes les décisions de recherche et de tous les prompts. Cet audit trouve notamment une catégorie hypothétique rendue immuable, une rupture du contexte conversationnel au passage serveur et une perte possible de contraintes dans les appels secondaires. Ces problèmes peuvent empêcher un modèle compétent de résoudre une demande sans être des limites du modèle.

## Chemins réellement atteints

1. `MainWindow/Messages.cs:589` appelle `RagChatAgent.RunAsync`. `MainWindow/SetupLifecycle.cs:804` instancie ce service : il ne s’agit pas d’une ancienne classe morte.
2. `Services/RagChatAgent.cs:240–278` construit au plus12 messages de contexte puis un `ToolAgentOrchestrator` pour le tour. `RunPipeline.cs:203–318` et `RouterCore.cs:76–160` font classifier puis construire la route avec de vrais contrats natifs. Le routeur voit la demande entière, avec un historique compact.
3. `RunPipeline.cs:428–483` applique la frontière locale. En mode local, les travaux au-delà de cette frontière produisent un handoff puis `RagChatAgent.cs:280–308` appelle le service avancé réel. Les modes de développement OpenAI/RunPod peuvent exécuter directement l’agent client : ils ne sont pas soumis au même arrêt local.
4. Pour le parcours documentaire client, `SourceBackedRag.cs:152–179` construit un `SourceBackedIntake`, puis `SourceBackedAgentV2.cs:35–108` construit le runner. L’initialisation, la préparation du tour, l’appel LLM, l’exécution des outils et la vérification sont séparés en étapes, mais partagent un état très large.
5. Le parcours avancé est distinct : le client sérialise le handoff ; le worker serveur transmet celui-ci, les preuves revalidées et les anciens événements d’outils au provider. Il ne réutilise pas automatiquement les prompts du runner V2 client. Un défaut client V2 ne doit donc pas être déclaré cause d’un essai Terra serveur sans vérifier l’entrée effectivement empruntée.

Tous les chemins de fichiers client cités ci-dessous sont relatifs à `C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/client/SAAIA.Client.WinUI/`. Les fichiers de runner se trouvent sous `ToolAgent/SourceBackedRag/`. Les références backend et contrats sont données depuis la racine du dépôt.

## C01 — P1 — Une hypothèse de catégorie devient une contrainte persistante de recherche

**Défaut de contrat statiquement prouvé, reproduction hors réseau requise avant correction.**

`SourceBackedAgentLlmScopeContinuity.cs:7–52` récupère `CandidateScopePaths` dans la mission portant `DecisionSource=llm_router`. Les lignes55–89 peuvent aussi le déduire du scope commun des premières actions LLM. `SourceBackedAgentV2RunInitialization.cs:133–135` conserve ce choix. L’état du runner le reçoit à l’initialisation (`V2Runner.InitializeExecution.cs:29`) ; aucun mécanisme de révision de cette propriété n’a été trouvé dans la boucle.

Quand la collection de candidats reste ouverte, `SourceBackedAgentDocumentaryToolBatch.cs:183–195` passe ce scope à `ApplyMechanicalToolArgumentAdjustments`. `SourceBackedAgentV2ToolArgumentAdjustments.cs:34–85` le réimpose sur recherche simple, recherche multiple, navigation et inventaire. Une catégorie différente explicitement choisie dans l’appel ultérieur est écrasée ; l’absence de catégorie est également remplacée. Le code distingue lui-même `llm_candidate_scope_applied` et `llm_candidate_scope_reapplied`.

Pourtant `SourceBackedAgentObservationCompactor.cs:128–142` propose explicitement `retry_without_category_scope` et `retry_with_another_exact_catalog_scope` après un rendement nul. Le prompt système, lignes81–86, dit au modèle de choisir lui-même cette récupération. La décision peut être produite puis annulée silencieusement avant exécution. La détection des doublons arrive après cette réécriture (`DocumentaryToolBatch.cs:221–229`), ce qui peut en plus transformer la tentative corrigée en doublon rejeté.

**Provenance :** le champ observé est une hypothèse de stratégie LLM, pas une preuve d’interdiction de licence ou une autorisation d’accès. La structure actuelle ne distingue pas suffisamment préférence de recherche et restriction obligatoire. Il ne faut pas supprimer les contrôles backend de tenant/ACL, ni élargir une recherche explicitement limitée par l’utilisateur à un document ou corpus donné. La correction doit rendre révisable la seule hypothèse de recherche et conserver une représentation séparée, typée et non contournable des restrictions obligatoires.

**Reproduction proposée :** fake LLM, deux catégories synthétiques A/B, mission LLM initiale avec A, premier `rag_search` en A sans résultat ; le LLM choisit ensuite B, puis un appel sans scope dans une variante. Le faux exécuteur enregistre les arguments effectifs. État rouge attendu : A reçu malgré la décision B ou sans scope. État vert : la décision révisée est préservée quand A n’était qu’une hypothèse. Ajouter un témoin où A est une restriction utilisateur explicite et un témoin de document nommé obligatoire : ces deux limites doivent rester opposables. Aucun accès à un vrai corpus n’est nécessaire.

## C02 — P1 — Le suivi conversationnel peut être perdu lors du transfert serveur

**Rupture de transport prouvée statiquement et rapprochée avec l’audit backend ; impact à reproduire sur un suivi contextualisé.**

Le routeur voit le contexte de conversation (`RouterCore.cs:94–102`). En revanche, `RunPipeline.cs:459–465` ajoute `IncludeUserInstanceContext` seulement pour `user_instance_context` et `user_reference_context`. Les grilles, listes et autres familles envoient `effectiveUserMessage` seul. `ClassifiedWorkContract.cs:61–71` confirme que ce complément exceptionnel ne transporte que les deux dernières déclarations utilisateur.

`AdvancedAnalysisHandoff.cs:72–123` ne contient aucun contexte conversationnel générique. Le contrat `contracts/SAAIA.Contracts/AdvancedAnalysisContracts.cs:5–32,34–82` transporte demande, charge et historique des recherches, pas les tours de conversation ni un état de contraintes utilisateur. Le worker passe `handoff`, preuves et événements précédents au provider (`backend/SAAIA.Backend/AdvancedAnalysis/AdvancedAnalysisWorker.cs:259–266`). Le prompt serveur utilise ensuite `Handoff.RequestText` (`OpenAiCompatibleAdvancedAnalysisProvider.PromptsAndEvidence.cs:287–303`). L’audit backend confirme qu’il ne recharge pas les messages de discussion pour compléter ce texte.

Exemple à vérifier : après « prépare un planning de repas pour3jours, sans arachides », le suivi « refais pareil avec des plats froids » peut être correctement classifié grâce à l’historique local, alors que le serveur ne reçoit plus la contrainte alimentaire initiale. Le descripteur de charge ne reconstitue pas nécessairement ce qui manque. Ce défaut ne s’applique pas à une demande autonome complète.

**Correction à préparer :** transporter un contexte utilisateur borné et versionné, ou une demande autonome reconstruite avec provenance explicite, séparé des preuves documentaires. Ne pas transformer la mémoire en preuve ni inventer des contraintes par résumé libre. **Test hors réseau :** sérialiser le vrai handoff d’un dialogue synthétique, reconstruire les messages serveur et vérifier que la contrainte initiale, la nouvelle instruction et leur provenance survivent ; vérifier aussi qu’une consigne explicitement révoquée n’est pas réintroduite.

## C03 — P1 — La demande entière ne survit pas à tous les appels secondaires ; un champ de contraintes reçoit des flags

**Deux faits statiquement prouvés ; effet exact sur une réponse à confirmer par faux LLM capturant les messages.**

Le contexte initial V2 conserve la demande entière (`SourceBackedAgentV2Prompts.cs:526–533`). Mais le contexte de travail réduit cette demande à900 caractères, ou600 en récupération (`:572–593`). Le rédacteur sélectionné la coupe à900 (`SourceBackedAgentV2WriterPromptContext.cs:44–45`) ; le rédacteur de grille à800 (`SourceBackedAgentStructuredCandidateWriter.cs:333–335`) ; les revues de candidats à360/420/500 selon le chemin (`SourceBackedAgentCandidateAuditReview.cs:128–132,236–250`). Ces plafonds sont indépendants de la fenêtre réellement disponible du fournisseur. Une condition déterminante située tard dans une demande peut donc être absente au moment de juger ou rédiger.

Le repli supposé « contraintes explicites » ne garantit pas sa conservation : `ToolAgentOrchestrator.SourceBackedRag.cs:152–156` fournit `routerPlan.RiskFlags` comme troisième argument positionnel de `SourceBackedIntake`, alors que ce paramètre est `ExplicitConstraints` (`SourceBackedPipelineContracts.cs:61–65`). Le rédacteur les présente comme **CONTRAINTES EXPLICITES** (`V2WriterPromptContext.cs:47–53`). Ce sont des flags internes, pas une extraction exhaustive et fidèle des contraintes utilisateur. Les noms sémantiques des champs ne correspondent donc pas à la donnée injectée.

**Reproduction proposée :** demande synthétique de plus de1 000 caractères avec une interdiction déterminante à la fin, contexte fournisseur assez grand, capture de tous les prompts avec un faux LLM ; vérifier tour d’action, revue et rédacteur, y compris récupération de contexte. Ajouter une demande courte comme témoin. La correction doit préserver une demande de référence complète quand elle tient et, sinon, une sélection explicite des contraintes sans les confondre avec les flags ; signaler toute troncature avec compteurs et identifiants, sans journaliser son contenu par défaut.

## C04 — P1 — Des journaux contournent la politique de contenu des sondes

**Écriture de contenu prouvée sur un chemin de production actif.**

`RunPipeline.cs:66–71` écrit les180 premiers caractères de la demande dans un `ClientLog.Info`. `SourceBackedRagToolExecutor.cs:99–105` écrit jusqu’à420 caractères des arguments réels, donc potentiellement requêtes, noms et chemins de documents. `Services/ClientLog.cs:39–50` écrit directement ces textes dans `client_startup.log`, sans consulter `ClientDiagnostics.IncludeContent` ni filtrer les contenus. Cela contourne la règle appliquée dans `RagTrace.cs:133–135`.

Les sondes structurées peuvent être en mode métadonnées tout en laissant du contenu utilisateur dans le journal de support. Ce constat n’affirme pas qu’un secret réel y a été observé : aucun journal privé ni secret n’a été lu dans cet audit.

**Correction ciblée :** utiliser les mêmes règles de contenu pour ces écritures, garder en mode normal outil, compteurs, code de raison, durée et empreintes non réversibles appropriées ; réserver le contenu à une option de diagnostic explicite. **Test :** faux texte confidentiel synthétique dans demande et arguments, écriture vers un répertoire temporaire de test ; absence dans les logs normaux, présence uniquement si le mode de contenu autorisé l’exige. Vérifier aussi le chemin de nouvelle exception.

## C05 — P2 — Plusieurs décisions utiles sont filtrées dans les sondes normales

**Perte de diagnostic statiquement prouvée ; portée distincte du correctif source_step déjà validé.**

`RagTrace.cs:158–166` admet une liste étroite de champs texte. `RunPipeline.cs:432–444` produit `capability_boundary.decision` avec `decision`, `plan_kind` et `provider_mode`, tous absents de cette liste : leurs valeurs sont éliminées en mode normal. Seuls certains compteurs et le code de raison restent. De même, les activations du pipeline et du runner utilisent encore `entry` (`SourceBackedRag.cs:111–116`, `SourceBackedAgentV2.cs:71–75`) ; le champ est filtré. Le correctif précédent concernait les deux sondes de replis et ne couvre pas ces appels.

Le sens de certaines mesures est également fragile : `RunPipeline.cs:48–64` résout un budget étendu pour un fournisseur avancé, alors que `SourceBackedAgentV2.cs:50` résout ses options sans descripteur avancé et publie leurs valeurs locales aux lignes83–90. Le budget effectif du parcours standard vient de l’AsyncLocal extérieur ; les valeurs publiées par le runner peuvent donc ne pas décrire ce budget effectif. Ce n’est pas la preuve que le parcours standard n’a aucun plafond.

**Correction :** exposer des identifiants sûrs existants ou des champs typés bornés pour la famille/branche, et publier l’instantané du budget effectivement utilisé. Ne pas admettre arbitrairement les arguments, requêtes ou plans. **Tests :** projection réelle en `metadata_only`, présence des codes attendus dans CLIENT_PROBE et RAG_TRACE, absence simultanée de marqueurs de contenu synthétiques ; vérifier fournisseur local puis fournisseur avancé factice sans appel réseau.

## C06 — P2 — Les limites de cardinalité ne sont pas cohérentes entre contrats

**Divergences statiques prouvées ; elles ne suffisent pas à expliquer un planning de7jours.**

Le schéma de route grille admet jusqu’à80 lignes et40 colonnes sous la limite globale de80 cellules (`NativeRouting/NativeRouterToolCatalog.cs:342–362`). Le parseur de dimensions de la sélection V2 limite les colonnes à12 (`SourceBackedAgentSemanticSelectionValidation.cs:99–104`) et les labels de colonnes à12 (`:125–129`). Le handoff limite les labels de lignes et colonnes à32 parce qu’ils réutilisent `AdvancedHandoffMaximumResearchItems` (`AdvancedAnalysisHandoff.cs:8,99–104`), tout en conservant `RowCount` et `ColumnCount` d’origine (`:87–88`).

Un contrat peut donc accepter une forme dont la suite ne conserve pas tous les axes. **Test proposé :** formes 40×1,1×13 et7×3 ; vérifier route, intake et handoff, et décider d’une limite commune explicite. Le test doit refuser clairement une forme hors capacité plutôt que perdre des labels. Aucun élargissement arbitraire de tous les plafonds n’est recommandé.

## C07 — P2 — Unicité des preuves et diversité des éléments sont partiellement confondues

**Limite fonctionnelle codée et certaine ; statut produit à décider, pas une régression native démontrée.**

La sélection rejette les valeurs finales identiques sans consulter une politique de diversité utilisateur (`SourceBackedAgentSemanticSelectionValidation.cs:43–61,341–364`). La sélection de type élément rejette aussi plusieurs entrées d’une même source visible (`:305–338`). Le prompt de grille impose au plus un EvidenceId par libellé identique (`SourceBackedAgentStructuredCandidateWriter.cs:358–372`) ; un patch de grille rejette les EvidenceId utilisés plusieurs fois (`SourceBackedAgentStructuredAssignmentRevisionContract.cs:67–79`).

Cela protège contre des doublons accidentels. Mais une demande explicite « réutilise ce même plat le mardi et le jeudi » n’est pas équivalente à une erreur de provenance. Les preuves doivent être uniques dans leur inventaire ; plusieurs usages autorisés du même élément dans le livrable peuvent être légitimes. Une politique de diversité canonique existe séparément, mais n’est pas consultée dans toutes ces règles inconditionnelles.

**Test de décision :** mêmes preuves avec trois demandes synthétiques : diversité explicite, réemploi explicite, demande ne précisant rien. Conserver strictement identité et citations, et déterminer si l’unicité appartient au contrat utilisateur ou à un défaut de sécurité mécanique. Ne pas supprimer globalement la détection des doublons avant d’avoir séparé ces concepts.

## C08 — P2 — Le runner public ne possède pas lui-même son budget cumulatif

**Fragilité d’architecture prouvée ; le parcours WinUI standard dispose bien d’un budget extérieur.**

La création de `SourceBackedLlmCumulativeBudget` et son `Push` se trouvent dans `RunPipeline.cs:53–65`. Le constructeur public de `SourceBackedAgentV2Runner` conserve ses options, mais `RunAsync` ne crée pas ce contexte. `SourceBackedLlmCumulativeBudgetContext.cs:73–75` exécute directement si le contexte est absent. Un harness ou une future entrée appelant directement ce runner peut donc renseigner ses plafonds dans les options sans obtenir l’admission cumulée attendue.

Le parcours WinUI normal ne présente pas ce défaut d’absence : il est enveloppé par l’orchestrateur. Le risque concerne les entrées alternatives et futurs essais utilisant directement le runner. Prévoir une unique propriété du budget au niveau du run : réutilisation explicite si un contexte existe, création sinon, sans doubler ni réinitialiser les dépenses d’un tour. **Test proposé :** runner seul puis runner sous budget déjà ouvert ; faux LLM consommant des jetons et preuve que chaque appel n’est compté qu’une fois.

## Hypothèses et limitations à mesurer avant d’accuser le modèle

**Extraits et observations.** Par défaut, les recherches exposent10 éléments et180 caractères par extrait, les cartes40 et240 (`SourceBackedAgentV2Options.cs:50–68`, `ObservationCompactor.cs:189–215`). Le rédacteur de grille ne reçoit que110 caractères d’extrait par candidat (`StructuredCandidateWriter.cs:375–383`). Les cartes canoniques sont préférées aux autres fragments présents dans le même lot (`ObservationCompactor.cs:223–253`). Cela peut masquer des ingrédients, négations ou conditions plus loin dans le texte. Ce n’est pas nécessairement un défaut : des lectures de contexte existent. Il faut vérifier si l’agent demande réellement ces lectures avant de juger une recette, plutôt que remplacer ces plafonds par un gros dump coûteux.

**Menus d’outils contraints.** Le prompt général dit que le modèle choisit librement le nombre et l’ordre d’outils (`V2SystemPrompt.cs:18–23`). Le menu réel dépend néanmoins d’un automate : collection, audit dédié, transition, sélection, décision terminale (`client/SAAIA.Client.Core/Execution/AgentToolMenuPlanner.cs:21–36`, `V2Runner.AcquireIterationCompletion.cs:145–178`). Certaines phases n’exposent plus les outils documentaires ordinaires. C’est une limite voulue pour borner et fiabiliser le protocole ; le risque est un texte de prompt qui promet une liberté absente. Il faut tester les menus par état et annoncer la capacité effective, sans retirer la réserve terminale ou les limites économiques.

**Mémoire bornée.** Les actions et sources précédentes sont bien séparées des preuves, mais le contexte mémoire est coupé à3 200/5 600/12 000 caractères (`V2Messages.cs:53–79`). L’avertissement indique que des détails restent dans la mémoire persistante, tandis que le catalogue d’outils documentaire n’offre pas de lecture générique de cette mémoire. Les ancres et notes ajoutées tard peuvent disparaître du prompt. C’est une hypothèse de coût/perte de continuité à tester sur dialogues longs ; ce n’est pas une preuve de corruption du stockage.

**Nombre d’appels.** Le routeur natif classe puis produit une route ; le plan de mission initial accepté peut éviter un nouveau plan (`SourceBackedAgentSemanticPlan.cs:30–66`). Les revues de rôles/candidats pré-observation sont déjà évitées quand des premières actions routeur existent (`SourceBackedAgentRunPlanning.cs:50–104`). Il serait inexact de compter systématiquement tous les sous-modèles possibles comme exécutés. Mesurer le graphe d’appels réellement emprunté avant de supprimer une revue ou d’augmenter le budget.

## Architecture : des fichiers courts ne suffisent pas à rendre le système simple

Le répertoire `ToolAgent` contient468 fichiers C# et125 981 lignes dans l’état lu. Le seul `ToolAgentOrchestrator` est partagé sur244 fichiers totalisant79 582 lignes ;142 fichiers déclarent une classe partielle `SourceBackedAgentV2Runner`. Les plus grands fichiers lus sont désormais de l’ordre de1 000–1 427 lignes, mais la responsabilité globale reste énorme. Le découpage physique n’a pas supprimé le couplage entre mémoire, heuristiques, sémantique, récupération, rendu et diagnostics.

Les doublons les plus concrets sont la construction de contexte/demande dans de nombreux prompts avec des plafonds différents, l’application du scope dans stratégie/menu/normalisation/exécuteur, et la politique de preuve/diversité répartie entre sélection, rédacteur, patch et vérificateur. L’incohérence C01 résulte précisément de ce partage de responsabilité.

Les deux grandes machines — orchestrateur historique et runner V2 — coexistent. `ApplyDocumentaryRagDefaults` est appelé à plusieurs endroits, mais ses garde-fous actuels respectent les missions LLM et la route générale (`DocumentaryRagDefaults.cs:10–40`) : cet audit n’y a pas trouvé une nouvelle reproduction du défaut général déjà corrigé. Il ne faut donc pas supprimer ces branches uniquement parce qu’elles semblent anciennes. L’adaptateur `RagChatAgent` reste actif et le provider l’utilise aussi pour des règles de format/max tokens : son nom historique ne prouve pas qu’il soit mort.

Refactoriser par propriétaires de contrats, avec tests de comportement : demande/contexte utilisateur ; portée autorisée et hypothèses de recherche ; protocole LLM/outils ; inventaire de preuves et usages ; budget du run ; diagnostics. Chaque extraction doit retirer une responsabilité de l’ancienne classe et avoir un témoin causal, plutôt que créer encore un fichier partiel.

## Capacités déjà présentes et garde-fous à conserver

- La recherche multiple existe réellement : `SourceBackedAgentToolCatalog.cs:74–85` transforme le champ `queries` en `rag.multi_search`. Le modèle n’est pas limité à une question/requête unique.
- Navigation/sommaire, inventaire de cartes, contexte ciblé et lecture groupée existent ; la navigation est explicitement non citable, les pages/chunks observés servent à obtenir une preuve.
- Le modèle peut réviser sa recherche et changer d’outil ; C01 montre une restriction incorrecte du scope, pas l’absence totale de boucle d’agent.
- Les identités document/révision/hash/page et la distinction mémoire/preuve sont conservées dans les contrats. `EvidenceItem.VisibleSourceKey:44–54` distingue correctement deux cartes canoniques de la même page par leur ContentCardId.
- Le service avancé met à jour mémoire, sources et dernière réponse lors d’un résultat terminé (`AdvancedAnalysisExecution.cs:11–17`). La reprise suit identité et révision du job ; aucune nouvelle anomalie de suppression ou de reprise terminale n’est démontrée ici.
- Les limites locales sont explicites : grille à partir de6 unités, liste à partir de5 hors extraction nommée bornée, comparaison documentaire hors enveloppe (`LocalCapabilityBoundary.cs:8–10,23–34,72–91`). Ce sont des décisions de capacité à conserver tant que des mesures justifient leur évolution.
- Les réserves terminales, compteurs cumulés et refus sûrs ne sont pas des plafonds à enlever pour « laisser raisonner ». Il faut réparer les informations, décisions et sonde effectives, puis mesurer le résultat.

## Ordre de travail recommandé avant tout essai payant

1. Geler cet audit et produire les témoins hors réseau C01/C02/C03 : mauvais scope corrigé, suivi conversationnel, contrainte tardive. Ces trois cas visent directement ce que reçoit le modèle et sa capacité à rechercher.
2. Corriger les seuls défauts démontrés ; conserver un témoin rouge, puis un vert où l’exécuteur et les prompts capturés montrent réellement la bonne information. Ajouter un témoin de restriction obligatoire et un de révocation de contrainte.
3. Fermer C04/C05 : confidentialité des journaux, branche, provenance du scope, compteur de troncature, menu réel, budget effectif. Vérifier les projections de diagnostic en mode normal, pas uniquement la présence du nom de sonde dans le source.
4. Réconcilier les limites de forme et clarifier la diversité ; tester les bornes mécaniques sans fournisseur. Les grandes extractions architecturales peuvent être progressives, avec un propriétaire par contrat.
5. Après les correctifs retenus et les suites adaptées, utiliser quelques essais économiques : demande simple, tâche composée courte, mauvais scope récupérable, suivi contextualisé, puis planning. Pour chaque essai, conserver empreinte de la demande complète, identité du modèle, menu effectivement envoyé, tailles/tokens, outils choisis et arguments effectifs, preuves disponibles/montrées et terminal choisi. Le contenu brut exige une politique explicite ; aucune clé ni mémoire privée ne doit se retrouver dans les preuves publiques.

Aucun des essais proposés n’est marqué exécuté ou réussi par ce rapport. Aucune promesse «100%» ni conclusion sur la capacité maximale du petit modèle ou de Terra n’en découle. Le statut produit reste **TESTE_NON_APPROUVE** et aucun paiement supplémentaire n’est engagé.
