# Revue exacte des prompts et outils — premier cas Luna

Date : 2026-10-25. Revue en lecture seule des deux traces exactes et du résultat terminal privés autorisés, rapprochés du code réellement atteint. Aucun appel réseau/modèle, service, compilation, test, configuration de déploiement ou secret ouvert pendant cette revue. Aucun code modifié. Les tests proposés ci-dessous ne sont pas exécutés par cette revue.

## Conclusion

Ce cas ne mesure pas la capacité de Luna à comprendre la recette : aucun passage documentaire ne lui a été présenté. Luna a produit un plan valide puis demandé une recherche reformulée valide. Le code a arrêté la recherche après ce premier suivi pour une demande non structurée, puis généré lui-même `insufficient_documentation` avant tout Writer ou Critic.

La protection qui interdit une réponse factuelle sans preuve reste nécessaire. Les défauts à traiter sont en amont : retrouver le document et son contenu, permettre une navigation réellement accessible quand la recherche lexicale ne donne rien, conserver les observations utiles, puis rendre au contrôleur un résultat de recherche et une possibilité de suite dans un budget explicite.

## Pièces et séquence observées

Job : `e6ccd421-918f-4be6-8326-7e0fc8d13a5c`.

Les pièces originales restent dans `local-fixture/runtime/private-traces/` et `local-fixture/runtime/terminal-result.private.json`. Elles ne sont ni copiées ni modifiées par ce rapport.

| Pièce | SHA-256 du fichier | Données constatées |
| --- | --- | --- |
| Trace `planner-ae0f80374030443384ccb9f1f90a26c8` | `9b9cd230d295db74b38f9ab0b0c718c335deff89305352d85e68f961bcdce9b9` | 1 tentative HTTP, 2 582 ms, 0,0003244 USD |
| Trace `research-review-abf2dbcbd0f34836844cb2a773b3076e` | `5619afb6281b400c3a1bb2ee942edcd148428aeea6a8e437a2921cfd34d112c7` | 1 tentative HTTP, 1 842 ms, 0,0005998 USD |
| Résultat terminal | `37051b42e7f435316e4ba27a8bf69a297821e2fa146ce853392959755dc689dc` | `succeeded`, `insufficient_documentation`, aucune claim ni evidence, 2 appels, 0,0009242 USD |

Les deux enveloppes désignent `gpt-5.6-luna`, `reasoning_effort=none`, une limite de 512 tokens de sortie et `finish_reason=stop`. Aucun refus, outil natif ou `tool_choice` n'apparaît dans les payloads. Le JSON des deux réponses est complet : aucune troncature de sortie n'est démontrée.

La demande porte sur le nombre de personnes et la quantité de vin rouge du bœuf bourguignon. Le nom `30-recettes-preferees-des-francais.pdf` figure dans `load.requestedDocumentName`, sans figurer dans le texte courant de la question. `conversationContext=null`, `revalidatedEvidenceCount=0`, une seule catégorie disponible : `Corpus`.

1. Le Planner reçoit un système de 5 799 caractères et un message utilisateur de 993 caractères. Il retourne une recherche portant le nom complet du PDF, le nom du plat et les termes nombre de personnes, quantité de vin et ingrédients. Il laisse correctement la catégorie vide. Son exemple de schéma dans le système ne montre pas `documentHint`, bien que le texte décrive ce paramètre.
2. `AddRequiredDocumentQueries` ajoute avant cette recherche une autre requête construite par le code : nom complet du PDF + question presque complète, avec `documentHint` imposé. Deux recherches sont visibles dans le message suivant, chacune avec ce hint, aucune avec un `sourceKey`.
3. Le Review reçoit un système de 10 144 caractères et un message utilisateur de 3 001 caractères. Le message contient `observations=[]`, aucune identité de source navigable, aucune plage de pages et aucun diagnostic de résolution documentaire ou de récupération. Il retourne `search_more`, avec `bœuf bourguignon ingrédients  personnes vin rouge`, `documentHint` correct et catégorie `Corpus`. Il a donc effectivement reformulé et retiré le nom de fichier du texte recherché.
4. Root confirme l'exécution de cette troisième recherche. Le résultat terminal ne contient aucune preuve et aucune claim. Il a été produit après seulement Planner et Review : aucun Writer/Critic n'a été appelé.

Le relecteur backend a confirmé séparément, à partir des diagnostics de récupération qu'il possède, zéro match brut des trois recherches, zéro retriever dégradé et aucun rejet de preuve après récupération. Il signale aussi que le précontrôle court `bourguignon` retournait deux hits sparse. Cette vérification n'a pas été rejouée par l'auteur de ce rapport.

## Ce que le modèle pouvait réellement faire

| Phase | Menu annoncé | Possibilité réelle dans ce cas |
| --- | --- | --- |
| Planner | `list_categories`, `search_corpus` | La liste a déjà été exécutée par le code. Le modèle peut uniquement retourner un plan JSON ; le code exécute les recherches. Aucun inventaire de documents ni passage n'est fourni. |
| Review | `search_corpus`, `read_source`, `find_source_text` | Une nouvelle recherche est admissible. La lecture et la recherche littérale exigent un `sourceKey` provenant des observations courantes ; aucune observation ne permet d'en obtenir un. Les inventer est interdit et rejeté par le parseur. |
| Writer/Critic | Branche ultérieure susceptible de demander de la recherche | Branche jamais atteinte ici, car le garde `evidence.Count == 0` retourne avant elle. |

`NativeResearchToolsEnabled=false` signifie absence de fonctions natives dans le protocole observé. Cela ne rend pas les opérations JSON fictives : la reformulation a bien été exécutée par le gateway. En revanche, le parcours n'est pas un agent libre de chaîner autant de consultations qu'il le juge utile ; ses possibilités sont celles de la machine d'état et de ses budgets.

Changer uniquement ce flag ne corrige pas les deux obstacles constatés. Les appels Planner/Review n'activent pas `allowNativeResearch`, et le retour sans preuve précède le Writer qui pourrait employer des outils natifs. Une navigation initiale sur une source connue manque toujours quand aucun extrait n'a encore été retrouvé.

## Causes établies dans le code

Les références sont relatives à la racine du dépôt, sources relues pendant cette revue.

### Arrêt après un seul suivi pour une demande directe

`backend/SAAIA.Backend/AdvancedAnalysis/OpenAiCompatibleAdvancedAnalysisProvider.cs:205` à `209` sort de la boucle après l'exécution du suivi lorsque `RequiresDistinctStructuredSelection` est faux. La demande réelle a `structuredLayout=false` : ce chemin s'applique, indépendamment du souhait du Review d'examiner de nouvelles observations. Une autre limite financière pourrait également borner les appels ; sa configuration n'a pas été ouverte pendant cette revue.

À `:218` à `227`, l'absence de preuve déclenche un retour immédiat. Le texte est le gabarit de `BuildNoEvidenceAnswer` à `:1224` à `1235`. Ce n'est pas une conclusion produite par Luna à partir de la recette. L'insuffisance est sûre sur le plan de la non-invention ; elle n'établit pas que le PDF ne contient pas la réponse.

### Identité connue, navigation dépendante d'un premier résultat de recherche

`AdvancedAnalysisToolGateway.cs:406` à `424` résout un `documentHint` en chemin effectif lorsqu'un seul document indexé correspond fortement. Ce chemin est conservé dans `requestForTrace`, et sert à la recherche. Ni le résultat de cette résolution ni une identité de source consultable ne sont remontés au contrôleur si aucun extrait n'est trouvé.

`OpenAiCompatibleAdvancedAnalysisProvider.Planning.cs:70` à `100` exige une observation courante pour `sourceKey`, `read_source` et `find_source_text`. Le gateway vérifie également que la source a déjà été observée. Ces garde-fous d'identité sont légitimes. Le défaut d'ergonomie agentique est l'absence d'une opération de découverte vérifiée qui puisse produire ce handle sans dépendre d'une recherche lexicale déjà réussie.

Le `sourceOverview` avec plage physique et nombre de chunks est attaché aux preuves résolues puis aux observations (`PromptsAndEvidence.cs:1121` à `1145`). Il n'existe donc pas dans le prompt réel vide, même si le système explique au modèle comment l'utiliser.

### Diagnostics de recherche perdus avant le message de retour

`AdvancedAnalysisToolGateway.cs:483` à `485` obtient les diagnostics et les dégradations, puis les conserve dans l'événement durable. Mais `AdvancedAnalysisSearchObservation` à `:42` à `51` ne porte pas la résolution documentaire ni `RetrievalDiagnostic`. `ExecuteSearchBatchAsync`, `OpenAiCompatibleAdvancedAnalysisProvider.cs:583` à `589`, ne réinjecte dans `priorSearches` que les diagnostics de lecture et de recherche littérale. Les dégradations retournées par l'observation n'entrent pas non plus dans le prompt de Review.

Enfin `BuildPriorSearchesForPrompt`, `PromptsAndEvidence.cs:662` à `678`, ne sérialise ni diagnostics de récupération ni état de résolution du hint. Le contrôleur voit donc des requêtes antérieures sans pouvoir distinguer document absent, document ambigu, source trouvée sans hit et échec d'un retriever. La dégradation n'est pas la cause réelle de ce cas, puisque le contrôle backend la donne absente ; sa perte reste un défaut de retour d'observation.

### Requêtes ajoutées et chargement de règles disproportionné

`Planning.cs:607` à `665` préfixe une requête mécanique nom de fichier + question et la place avant les requêtes du modèle. `ResolveMaximumPlanQueries`, `:842` à `843`, borne ce cas à deux requêtes par batch. Le nom du document figure donc à la fois comme filtre `documentHint` et comme texte recherché dans les deux premières recherches.

Le système Planner demande explicitement de conserver l'identifiant complet dans la requête ; ce comportement ne peut pas être imputé au seul modèle. Cette duplication peut pénaliser une récupération lexicale conjonctive. Elle ne suffit pas à expliquer la troisième recherche, qui ne contient plus le nom de fichier et donne pourtant zéro résultat.

Les longs systèmes incluent beaucoup de règles de grilles, de diversité, d'affectations et de candidats pour une question factuelle simple. Cela augmente le coût et la complexité du contrat. Aucun défaut de compréhension ou dépassement de contexte n'est cependant prouvé par ces deux sorties valides.

## Hypothèses encore à isoler

- Différence de normalisation entre texte indexé et requête : ligature `œ`/`oe`, accentuation, ponctuation, espaces/OCR et lexèmes exacts. L'échec de la recherche courte à plusieurs termes, malgré le hit de `bourguignon`, nécessite de comparer les lexèmes et non de supposer que tous les mots visibles sont identiques.
- Effet du `documentHint` et de la catégorie sur le chemin effectivement recherché : document exact, zéro ou plusieurs candidats, absence de scope en cas d'ambiguïté, catégorie effective. Le contrôleur ne reçoit actuellement pas cette information. Aucun diagnostic de fixture ou secret n'a été ouvert par cette revue.
- La coexistence de recherches exactes/sparse/dense toutes vides ne prouve pas à elle seule une faiblesse du modèle ; elle peut résulter d'un même filtre ou d'une admission commune. L'inspection backend doit établir l'étape précise.
- `reasoning_effort=none` est constaté, mais augmenter l'effort ne crée ni passage absent du contexte, ni handle autorisé, ni tour de recherche que le code ne propose pas.

## Tests causaux hors réseau à faire avant un nouvel essai payant

1. Rejouer les deux réponses capturées via un faux transport et un gateway déterministe : deux recherches initiales vides, première reformulation vide, puis une deuxième stratégie qui retourne la preuve. Vérifier le point d'arrêt actuel et exiger une continuation explicitement bornée, avec réserve de synthèse et sans appels identiques répétés. Variante témoin : épuisement réel du budget => arrêt explicite, jamais preuve d'absence documentaire.
2. Tester un document indexé connu mais aucun hit lexical : découverte documentaire vérifiée => handle observé + identité/version/hash + plage réelle => `find_source_text` ou `read_source`. Variantes indispensables : document ambigu, autre tenant, révision remplacée, mauvais hash, scope utilisateur imposé. Aucune lecture arbitraire par nom inventé ne doit devenir possible.
3. Préparer une matrice locale de récupération sur le même document et les mêmes paramètres : `bourguignon`, nom exact du plat, variantes `bœuf`/`boeuf`, puis ajout progressif de `ingrédients`, `personnes`, `vin`, `rouge`. Capturer tokens/lexèmes, scope effectif, matches bruts, hits retenus et étape qui les élimine. Comparer enfin texte avec/sans nom de fichier, à `documentHint` constant. Ce contrôle doit séparer cause de requête et cause de filtrage.
4. Tester le retour d'observation : document résolu sans hit, document ambigu, retriever dégradé, résultat brut rejeté à la revalidation. Exiger des codes distincts réellement présents dans le prompt suivant et les sondes ordinaires, sans contenu documentaire dans les journaux ordinaires.
5. Rejouer une preuve canonique connue du cas, fournie par le harness et non injectée comme connaissance libre : Writer doit répondre aux deux faits demandés, citer la preuve actuelle et ne pas confondre une page d'index avec le corps de la recette. La cible 4 personnes / 1,5 L / page physique 5 est une attente communiquée par root ; ce rapport n'a pas relu ni validé la page source elle-même.
6. Vérifier le contrat et les budgets avec les outils natifs activés et désactivés. Le simple changement du flag ne doit pas être présenté comme une réparation de la navigation sans handle. Tester séparément arrêt sans preuve, possibilité de continuer, contrôle d'identité et coût maximal.

Ces tests sont proposés, non exécutés dans cette revue. La priorité reste d'expliquer le troisième zéro brut, puis de corriger la continuité et le retour d'observation sans retirer les garanties de provenance ni élargir silencieusement les budgets.
