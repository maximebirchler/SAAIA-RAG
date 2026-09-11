# Goal — SAAIA RAG produit documentaire end-to-end

> Date d'activation : 2026-08-27 10:41 Europe/Zurich  
> Statut : **ACTIF**  
> Remplace comme Goal opérationnel : le texte embarqué dans
> `PLAN-ACTION-DYNAMIQUE-2026-08-26-RAG-END-TO-END.md` avant le réalignement du
> 2026-08-27.  
> Références d'architecture conservées :
> `GOAL-2026-07-28-RAG-CANONIQUE-LLM-FIRST.md` et
> `ADR-2026-07-08-rag-llm-orchestration-source-backed.md`.

## Objectif complet

Achever le logiciel documentaire SAAIA RAG de bout en bout afin qu'un
utilisateur puisse interroger en langage naturel ses corpus privés indexés et
recevoir, dans WinUI, une réponse utile, exacte, honnête et vérifiable,
exclusivement fondée sur les documents actuellement disponibles. Chaque
affirmation documentaire significative doit pouvoir être reliée à une preuve
canonique et à une carte source cliquable ouvrant exactement le bon fichier,
la bonne révision, la bonne page et le bon passage.

Qwen3 côté client reste l'orchestrateur et le décideur sémantique principal. Il
comprend l'intention de l'utilisateur, choisit les outils et les requêtes,
décide des documents et zones à explorer, interprète les observations, juge la
pertinence et la complémentarité des preuves, révise son plan de recherche,
décide de paginer, pivoter, clarifier, répondre partiellement ou déclarer une
insuffisance, affecte les preuves aux éléments du livrable et rédige la réponse
finale. Aucun outil, ordre fixe d'outils, nombre d'appels ou stratégie de
recherche ne doit être imposé lorsque cette décision dépend du sens de la
demande ou du contenu découvert.

Le code et le backend restent responsables des opérations mécaniques,
transparentes et testables : ingestion, OCR, indexation, révisions, isolation
du tenant, validation des contrats, résolution des identités, pagination,
calculs et scores déclarés, déduplication canonique, budgets de temps et de
contexte, gestion des erreurs, traçabilité, transport des preuves et
vérification de l'existence des citations. Le code ne doit pas décider à la
place de Qwen3 qu'une source est sémantiquement pertinente, qu'un document
répond à une question ou qu'un candidat convient à un rôle métier.

Toute la chaîne source-backed doit utiliser une représentation canonique unique
des preuves, l'`EvidenceBundle`, depuis les résultats documentaires jusqu'au
writer, au vérificateur de sources et aux cartes WinUI. Les identités `docId`,
`revisionId`, fichier, page, `chunkId`, `anchorId`, `contentCardId`,
`EvidenceId` et les hashes nécessaires doivent être conservées sans
reconstruction approximative ni perte silencieuse. Le writer ne peut utiliser
que les preuves visibles dans cet `EvidenceBundle`. Le vérificateur final
refuse mécaniquement les citations inexistantes, invisibles, dupliquées ou
incohérentes, mais ne remplace pas le jugement sémantique du LLM.

La mémoire doit permettre la continuité entre les tours et, lorsque prévu par
le produit, entre les discussions d'un même projet. Elle conserve notamment le
but actif, les contraintes, les requêtes exécutées, les zones explorées, les
sources observées, les candidats acceptés ou rejetés, leurs motifs et les
éléments déjà livrés. Elle doit être compacte, typée, bornée et récupérable à
la demande. La mémoire n'est jamais une preuve documentaire : toute information
utilisée comme fait dans une nouvelle réponse doit être à nouveau reliée à une
preuve actuelle du corpus.

L'architecture doit rester généraliste. Aucune logique de production ne doit
être spécialisée pour Cuisine, les recettes, les repas, Q019, un document
particulier, une catégorie, une langue, un nom de fichier ou une liste connue
de contenus. Le planning de cinq jours × quatre repas reste un stress-test
important de recherche longue, de diversité, d'affectation et de provenance,
mais il ne constitue pas le but du logiciel et ne doit pas dicter
l'architecture générale.

La réalisation doit partir de la chaîne complète vue par l'utilisateur, et non
d'un composant isolé considéré artificiellement comme le produit. Chaque
diagnostic doit mesurer où une information correcte apparaît, où elle est
perdue et pourquoi, depuis l'intention jusqu'à l'affichage final. Une
amélioration locale n'est conservée que si son effet positif est démontré sur le
fonctionnement end-to-end, sans régression des scénarios simples, nommés,
ambigus, hors corpus ou non Cuisine.

Le registre dynamique
`PLAN-ACTION-DYNAMIQUE-2026-08-26-RAG-END-TO-END.md` reste le journal
opérationnel vivant du chantier, mais il est strictement subordonné au présent
Goal. Il consigne les phases, hypothèses, modifications, commandes,
configurations, métriques, artefacts, inspections humaines, régressions et
décisions. Il ne peut pas modifier l'objectif produit, rendre obligatoire une
solution expérimentale particulière ou bloquer le Goal sur une branche
facultative. Les anciens résultats, y compris les expériences de routeur et de
comparaison de modèles, sont conservés comme historique ; les branches
distantes, payantes, matérielles ou de fine-tuning sont classées comme options
facultatives et non bloquantes tant qu'elles ne sont pas explicitement
autorisées par l'utilisateur.

Le travail avance phase par phase. Une seule phase d'implémentation est active.
Une phase n'est approuvée que lorsque ses critères sont démontrés par des tests
déterministes appropriés, des exécutions live lorsque le comportement du LLM
est concerné, une inspection humaine lorsque la qualité sémantique ou visuelle
est concernée, et des artefacts reproductibles. Un test vert accompagné d'un
résultat qualitativement incorrect reste `TESTEE_NON_APPROUVEE`. Une régression
rouvre la phase responsable. Une phase ne peut être déclarée `BLOQUEE` que
lorsqu'aucun travail local sûr et pertinent ne peut encore progresser sans une
véritable décision ou dépendance externe.

La première phase réaligne le registre avec ce Goal, retire le faux blocage lié
aux modèles distants, préserve tout l'historique et inventorie les preuves
encore valides. Elle fige ensuite une baseline reproductible du dépôt, des
services, du corpus, des révisions, du runtime Qwen3, des paramètres matériels
et du binaire utilisé. Le chantier reprend à la première lacune réellement non
satisfaite ; il ne recommence pas inutilement les validations encore valides.

La deuxième phase établit une baseline verticale locale complète sur plusieurs
demandes représentatives : question utilisateur, orchestration Qwen3, appels
documentaires, retrieval, `EvidenceBundle`, writer, vérification des citations,
`sourcesPayload` et rendu WinUI. Elle identifie causalement les pertes,
transformations ou décisions incorrectes à chaque frontière. L'optimisation du
premier choix d'outil n'est traitée comme prioritaire que si cette baseline
démontre qu'il constitue effectivement le goulot principal.

La troisième phase stabilise l'orchestration adaptative et généraliste de
Qwen3. Elle garantit que les hypothèses provisoires ne deviennent pas
silencieusement des arguments d'outils, que les observations réelles peuvent
modifier le plan de recherche, que les ambiguïtés importantes provoquent une
clarification utile et que les demandes suffisamment définies ne sont pas
ralenties par des clarifications inutiles. Les questions simples et les
documents nommés doivent emprunter un chemin proportionné à leur complexité.

La quatrième phase stabilise la recherche multiétape et la mémoire de
rendement. Qwen3 reçoit un état compact des routes, requêtes, offsets,
rendements, zones explorées, preuves acceptées ou rejetées et besoins encore
non couverts. Il peut poursuivre, paginer, pivoter, changer d'outil, arrêter la
recherche ou déclarer une insuffisance. Les répétitions mécaniquement identiques
et les boucles sans rendement sont empêchées sans imposer une décision
sémantique au modèle.

La cinquième phase garantit l'intégrité verticale des preuves. Une preuve
trouvée ne doit plus pouvoir disparaître ou perdre son identité entre
retrieval, normalisation, sélection, writer, vérification et interface. Les
réponses, citations et cartes source doivent toutes dériver du même
`EvidenceBundle`. Les réparations éventuelles sont ciblées sur les éléments
refusés et ne régénèrent pas arbitrairement les éléments déjà correctement
prouvés.

La sixième phase valide les comportements fonctionnels généraux : questions
factuelles simples, documents explicitement nommés, navigation dans des
documents longs, demandes nécessitant plusieurs sources, ambiguïtés, hors
corpus, suivis conversationnels, non-répétition, autres catégories et autres
langues. Elle valide aussi les contrats des différentes mémoires, leur
isolation entre utilisateurs, tenants et projets, leur compaction et leur
comportement sous pression de contexte.

La septième phase exécute les validations live end-to-end et les benchmarks de
performance. Sur la machine P520, les objectifs sont une médiane inférieure ou
égale à 30 secondes pour une question simple, 45 secondes pour un document
nommé et sept minutes pour le planning complexe, sans run de planning supérieur
à dix minutes. Aucun dépassement de contexte ou crash mémoire n'est accepté.
Toute cible non atteinte doit être accompagnée d'une mesure du goulot réel et
de la meilleure variante comparée, sans compensation aveugle par davantage de
contexte, de tours, de timeout ou de prompts.

Le planning de cinq jours × quatre repas est approuvé uniquement lorsque le
corpus permet réellement vingt propositions concrètes, nommées, distinctes et
adaptées à leurs créneaux, chacune associée à une preuve et à une carte source
exacte. Si vingt éléments valides n'existent pas, le système doit signaler
précisément l'insuffisance au lieu d'inventer ou de transformer un ingrédient,
une rubrique, un sommaire ou un fragment OCR en recette. Les scénarios
probabilistes critiques nécessitent trois réussites live consécutives sur un
état figé.

La huitième phase consolide le produit et la dette historique. Elle identifie
l'unique chemin source-backed de production, supprime uniquement les voies
mortes prouvées, retire les fallbacks historiques devenus inutiles, modularise
les monolithes, migre les anciennes cartes avant toute purge, conserve les
modifications utilisateur sans rapport et produit des commits petits,
cohérents, vérifiés et réversibles. Aucun nettoyage global du worktree n'est
permis.

La neuvième phase valide le parcours WinUI réel : envoi des demandes, affichage
des réponses, comportement de la mémoire, erreurs et insuffisances, rendu des
sources et ouverture de chaque carte sur le bon document et la bonne page. La
clôture exige des builds et suites de tests verts, `git diff --check` propre,
aucune citation inventée, aucune preuve issue uniquement de la mémoire, aucune
duplication canonique, aucun fallback historique actif, aucune règle métier
spécifique aux scénarios de test et un rapport final reproductible précisant
versions, configurations, commandes, métriques, artefacts, limites résiduelles
et état Git.

Des rapports Telegram compréhensibles doivent être envoyés aux paliers
réellement importants : approbation ou réouverture d'une phase, découverte
d'une régression critique, décision architecturale majeure, résultat
end-to-end significatif et véritable blocage nécessitant une action de
l'utilisateur. Ces rapports doivent expliquer en langage clair ce qui a été
tenté, observé, conservé, rejeté et ce qui suit. Les identifiants internes comme
`EXP-xxx`, `PRV-xxx` ou `DEC-xxx` peuvent servir à la traçabilité, mais ne
doivent jamais remplacer l'explication destinée à l'utilisateur.

Le chantier n'est terminé que lorsque l'utilisateur peut poser des demandes
documentaires simples ou complexes et obtenir dans SAAIA des réponses
correctes, proportionnées, traçables et vérifiables, avec des sources exactes
réellement ouvrables dans WinUI. Si une même classe d'échec réapparaît, arrêter
l'empilement de correctifs locaux, remesurer toute la chaîne, comparer des
architectures génériques et conserver uniquement la solution dont
l'amélioration end-to-end est démontrée.

## Hiérarchie d'autorité

1. Le présent Goal fixe l'objectif produit et les invariants durables.
2. Les ADR fixent les décisions d'architecture compatibles avec ce Goal.
3. Le plan dynamique pilote l'exécution et conserve les preuves.
4. Les expériences, décisions et variantes du registre n'ont jamais autorité
   pour redéfinir le produit ni pour rendre obligatoire une branche facultative.

