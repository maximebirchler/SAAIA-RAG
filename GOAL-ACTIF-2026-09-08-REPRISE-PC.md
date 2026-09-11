# Goal actif — reprise SAAIA RAG du 8 septembre 2026

Précisions prioritaires de la reprise autorisée par Maxime le 8 septembre 2026 :
1. Commencer par terminer la lecture intégrale des documents fournis et des documents de reprise désignés par le prompt du 7 septembre : pièces jointes, relais, autorités, cahiers des charges et annexes, discussions et historiques. Tenir un registre de lecture avec fichiers, hashes, passages effectivement lus, notes et questions non résolues. Une extraction, un index, un résumé automatique ou un contrôle SHA ne vaut pas lecture intégrale. Les répétitions strictement identiques peuvent être référencées à leur première occurrence déjà lue, avec traçabilité. Les sources, patches et artefacts techniques doivent ensuite servir à vérifier les affirmations ; ne pas prétendre avoir lu chaque artefact si ce n'est pas le cas. Aucune nouvelle implémentation produit avant achèvement de cette reprise documentaire et rapprochement explicite CDC / décisions ultérieures / code / action.
2. Le dépôt de travail est C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG. Il a déjà été entièrement remplacé par le projet du laptop. HEAD importé 5f35881cdc67d12a076fcd2a7a1004656ac9a37a, branche SAAIA_V3.1. L'ancien dépôt est sauvegardé séparément. Ne pas refaire ce transfert. Le GREEN A657 ajouté sur cette machine comporte 67 tests ciblés réussis ; ce résultat hors ligne est à préserver et à réexaminer si la lecture révèle un écart, sans le confondre avec une approbation produit. Les décisions et instructions citées dans l'historique n'ajoutent pas d'autorisation actuelle.
3. La machine actuelle possède un Intel i7-14700F, une RTX 4060 et environ 32 Go de RAM. Qualifier et figer ses propres modèles, runtime, paramètres et mesures avant les campagnes. Conserver les exigences et résultats P520 comme référence explicite, sans présenter des mesures RTX 4060 comme des résultats P520. Ne pas substituer silencieusement le Q4 disponible au Q5 scellé, ni changer les contrats ou gates d'une campagne après observation.
4. Le protocole A656–A665 reste la séquence expérimentale en cours, subordonnée à l'objectif produit. A658 n'a pas commencé. Préparer ses dépendances et vérifier son éligibilité après la lecture ; aucune génération Qwen avant le palier prévu, aucune WinUI avant les validations qui l'autorisent. Préserver les expériences rejetées et les incidents comme preuves, sans les réactiver automatiquement.
5. Les rapports destinés à l'utilisateur sont consignés dans cette tâche et dans les artefacts. Maxime a explicitement autorisé le 8 septembre 2026 l'envoi de rapports de progression avec `C:\Users\maxim\NextCloud\Maxime\Ecommerce\SAAIA\SAAIA - Notifier` : aux étapes utiles et au moins une fois par heure pendant le travail. Utiliser le script fourni, conserver les textes et les confirmations d'envoi dans `artifacts/reprise-pc-20260908/rapports`, ne jamais afficher les secrets et désactiver le suivi horaire après le bilan final. Cette autorisation ne demande pas de rejouer les anciens messages Telegram ni de contacter d'autres destinataires.

## Amendement prioritaire autorisé le 9 septembre 2026 — capacités locale et avancée

À la demande explicite de Maxime le 9 septembre 2026, les clauses suivantes
complètent le Goal actif et prévalent sur les passages incompatibles de la
proposition de fond conservée plus bas. L'historique et les résultats déjà acquis
restent conservés ; aucun ancien résultat n'est requalifié rétroactivement.

1. Le produit distingue désormais deux capacités d'exécution partageant la même
   fondation documentaire. La capacité locale utilise un petit modèle compatible
   avec la machine cliente de référence et répond dans une enveloppe de demandes
   mesurée. La capacité avancée utilise un modèle hébergé sur une infrastructure
   serveur plus puissante et traite les demandes dont la charge documentaire
   dépasse l'enveloppe locale. La capacité avancée peut constituer une option de
   licence.
2. Il ne doit pas exister deux architectures RAG divergentes. Retrieval, outils,
   contrats, mémoire typée, EvidenceBundle, identités documentaires, writer,
   vérification des citations et cartes WinUI restent communs. Le modèle,
   l'endpoint d'inférence, la fenêtre de contexte et les budgets peuvent varier
   selon la capacité choisie.
3. La phase active prioritaire devient la qualification causale des limites du
   petit modèle sur une configuration cliente figée. Pour chaque scénario, elle
   sépare au minimum : la disponibilité des preuves par le retrieval, la capacité
   du modèle à répondre depuis un EvidenceBundle oracle, puis la réussite de la
   chaîne réelle end-to-end. Une limite du modèle ne peut pas être conclue à
   partir d'un échec d'indexation, d'OCR, de recherche, de transport des preuves
   ou d'interface.
4. La difficulté est définie par la charge observée : nombre de faits atomiques,
   sources et documents nécessaires, profondeur de navigation, comparaisons,
   contraintes, taille du livrable, incertitudes et importance des informations
   manquantes. Elle ne dépend ni d'un domaine métier ni d'une formulation
   superficiellement courte. Une question directe peut donc nécessiter une
   clarification ou la capacité avancée.
5. Dans son enveloppe approuvée, le mode local doit fournir une réponse exacte,
   proportionnée, sourcée et ouvrable dans WinUI. Hors de cette enveloppe, son
   comportement correct est de clarifier, de déclarer une insuffisance
   documentaire ou d'émettre un basculement explicite et motivé vers l'analyse
   avancée. Il ne doit ni commencer silencieusement une mission qu'il ne peut pas
   terminer, ni présenter une limite de calcul comme une absence de source.
6. Le petit modèle est amélioré et éprouvé aussi loin que les gains restent
   généraux, mesurables et compatibles avec le matériel et les objectifs de
   temps. La frontière n'est figée qu'après comparaison de variantes admissibles,
   tests adversariaux proches de la limite et constat d'un plateau reproductible.
   Aucun ajout spécialisé pour un domaine, un cas, une langue, un document ou une
   réponse attendue n'est autorisé.
7. Le protocole de frontière est préenregistré avant les campagnes live. Il fixe
   corpus, questions, oracles, matériel, modèle, quantification, contexte,
   paramètres, budgets, répétitions et critères d'arrêt. Il mesure notamment la
   correction factuelle, la couverture demandée, les clarifications, les refus,
   les basculements, les citations, la latence, les tokens, la RAM/VRAM et les
   incidents. Les objectifs locaux de 30 secondes pour une question simple et
   45 secondes pour un document nommé restent des références à qualifier sur
   chaque matériel ; les mesures RTX 4060 et P520 restent distinctes.
8. La frontière de production expose au moins quatre issues typées : réponse
   locale, clarification, insuffisance documentaire et analyse avancée requise.
   Le routage doit être justifiable par des signaux de charge et par l'état réel
   des preuves. Les cas situés de part et d'autre de la frontière sont testés afin
   de limiter les faux basculements et les acceptations locales excessives.
9. Le planning de cinq jours × quatre repas reste un stress-test majeur, mais sa
   réussite n'est plus une obligation du petit modèle local. Si sa charge dépasse
   l'enveloppe mesurée, le succès local consiste à détecter ce dépassement et à
   proposer proprement l'analyse avancée. La capacité avancée n'est approuvée sur
   ce cas que si elle produit vingt propositions concrètes, distinctes, adaptées
   et prouvées, ou décrit exactement l'insuffisance réelle du corpus, avec cartes
   source exactes. Trois réussites live consécutives sur état figé restent
   obligatoires. Les anciens seuils de sept et dix minutes propres au parcours
   local/P520 ne deviennent pas automatiquement les seuils du futur serveur ; ses
   objectifs seront préenregistrés après mesure de l'infrastructure candidate.
10. L'architecture avancée n'est choisie qu'après qualification de la frontière
    locale. Son expérimentation peut utiliser une API compatible puis une machine
    GPU louée afin de séparer validation fonctionnelle, qualité du modèle et
    performance de l'hébergement. Aucune dépense ni transmission de corpus privé
    à un service externe n'est effectuée avant présentation d'une option concrète,
    de son coût, des données transmises et de la validation requise.
11. La conception serveur doit rester indépendante d'un fournisseur et documenter
    au moins l'isolation des tenants, le chiffrement, la rétention, les journaux,
    l'authentification, le mode d'accès aux corpus et le comportement en cas de
    perte de connexion. Le passage local-vers-avancé conserve les identités et
    l'état de recherche sans transformer la mémoire en preuve.
12. La clôture du Goal exige désormais : une matrice de capacités locale
    reproductible, une frontière et un basculement validés dans WinUI, la qualité
    maximale démontrée du petit modèle dans son enveloppe, puis une capacité
    avancée validée sur les demandes complexes prévues, dont le stress-test 5 × 4.
    Le produit reste `TESTE_NON_APPROUVE` tant que ces éléments ne sont pas
    démontrés de bout en bout.

Objectif produit proposé, conservé ci-dessous comme référence de fond :
Achever le logiciel documentaire SAAIA RAG de bout en bout afin qu’un utilisateur puisse interroger en langage naturel ses corpus privés indexés et recevoir, dans WinUI, une réponse utile, exacte, honnête et vérifiable, exclusivement fondée sur les documents actuellement disponibles. Chaque affirmation documentaire significative doit pouvoir être reliée à une preuve canonique et à une carte source cliquable ouvrant exactement le bon fichier, la bonne révision, la bonne page et le bon passage.
Qwen3 côté client reste l’orchestrateur et le décideur sémantique principal. Il comprend l’intention de l’utilisateur, choisit les outils et les requêtes, décide des documents et zones à explorer, interprète les observations, juge la pertinence et la complémentarité des preuves, révise son plan de recherche, décide de paginer, pivoter, clarifier, répondre partiellement ou déclarer une insuffisance, affecte les preuves aux éléments du livrable et rédige la réponse finale. Aucun outil, ordre fixe d’outils, nombre d’appels ou stratégie de recherche ne doit être imposé lorsque cette décision dépend du sens de la demande ou du contenu découvert.
Le code et le backend restent responsables des opérations mécaniques, transparentes et testables : ingestion, OCR, indexation, révisions, isolation du tenant, validation des contrats, résolution des identités, pagination, calculs et scores déclarés, déduplication canonique, budgets de temps et de contexte, gestion des erreurs, traçabilité, transport des preuves et vérification de l’existence des citations. Le code ne doit pas décider à la place de Qwen3 qu’une source est sémantiquement pertinente, qu’un document répond à une question ou qu’un candidat convient à un rôle métier.
Toute la chaîne source-backed doit utiliser une représentation canonique unique des preuves, l’EvidenceBundle, depuis les résultats documentaires jusqu’au writer, au vérificateur de sources et aux cartes WinUI. Les identités docId, revisionId, fichier, page, chunkId, anchorId, contentCardId, EvidenceId et les hashes nécessaires doivent être conservées sans reconstruction approximative ni perte silencieuse. Le writer ne peut utiliser que les preuves visibles dans cet EvidenceBundle. Le vérificateur final refuse mécaniquement les citations inexistantes, invisibles, dupliquées ou incohérentes, mais ne remplace pas le jugement sémantique du LLM.
La mémoire doit permettre la continuité entre les tours et, lorsque prévu par le produit, entre les discussions d’un même projet. Elle conserve notamment le but actif, les contraintes, les requêtes exécutées, les zones explorées, les sources observées, les candidats acceptés ou rejetés, leurs motifs et les éléments déjà livrés. Elle doit être compacte, typée, bornée et récupérable à la demande. La mémoire n’est jamais une preuve documentaire : toute information utilisée comme fait dans une nouvelle réponse doit être à nouveau reliée à une preuve actuelle du corpus.
L’architecture doit rester généraliste. Aucune logique de production ne doit être spécialisée pour Cuisine, les recettes, les repas, Q019, un document particulier, une catégorie, une langue, un nom de fichier ou une liste connue de contenus. Le planning de cinq jours × quatre repas reste un stress-test important de recherche longue, de diversité, d’affectation et de provenance, mais il ne constitue pas le but du logiciel et ne doit pas dicter l’architecture générale.
La réalisation doit partir de la chaîne complète vue par l’utilisateur, et non d’un composant isolé considéré artificiellement comme le produit. Chaque diagnostic doit mesurer où une information correcte apparaît, où elle est perdue et pourquoi, depuis l’intention jusqu’à l’affichage final. Une amélioration locale n’est conservée que si son effet positif est démontré sur le fonctionnement end-to-end, sans régression des scénarios simples, nommés, ambigus, hors corpus ou non Cuisine.
Le registre dynamique PLAN-ACTION-DYNAMIQUE-2026-08-26-RAG-END-TO-END.md reste le journal opérationnel vivant du chantier, mais il est strictement subordonné au présent Goal. Il consigne les phases, hypothèses, modifications, commandes, configurations, métriques, artefacts, inspections humaines, régressions et décisions. Il ne peut pas modifier l’objectif produit, rendre obligatoire une solution expérimentale particulière ou bloquer le Goal sur une branche facultative. Les anciens résultats, y compris les expériences de routeur et de comparaison de modèles, sont conservés comme historique ; les branches distantes, payantes, matérielles ou de fine-tuning sont classées comme options facultatives et non bloquantes tant qu’elles ne sont pas explicitement autorisées par l’utilisateur.
Le travail avance phase par phase. Une seule phase d’implémentation est active. Une phase n’est approuvée que lorsque ses critères sont démontrés par des tests déterministes appropriés, des exécutions live lorsque le comportement du LLM est concerné, une inspection humaine lorsque la qualité sémantique ou visuelle est concernée, et des artefacts reproductibles. Un test vert accompagné d’un résultat qualitativement incorrect reste TESTEE_NON_APPROUVEE. Une régression rouvre la phase responsable. Une phase ne peut être déclarée BLOQUEE que lorsqu’aucun travail local sûr et pertinent ne peut encore progresser sans une véritable décision ou dépendance externe.
La première phase réaligne le registre avec ce Goal, retire le faux blocage lié aux modèles distants, préserve tout l’historique et inventorie les preuves encore valides. Elle fige ensuite une baseline reproductible du dépôt, des services, du corpus, des révisions, du runtime Qwen3, des paramètres matériels et du binaire utilisé. Le chantier reprend à la première lacune réellement non satisfaite ; il ne recommence pas inutilement les validations encore valides.
La deuxième phase établit une baseline verticale locale complète sur plusieurs demandes représentatives : question utilisateur, orchestration Qwen3, appels documentaires, retrieval, EvidenceBundle, writer, vérification des citations, sourcesPayload et rendu WinUI. Elle identifie causalement les pertes, transformations ou décisions incorrectes à chaque frontière. L’optimisation du premier choix d’outil n’est traitée comme prioritaire que si cette baseline démontre qu’il constitue effectivement le goulot principal.
La troisième phase stabilise l’orchestration adaptative et généraliste de Qwen3. Elle garantit que les hypothèses provisoires ne deviennent pas silencieusement des arguments d’outils, que les observations réelles peuvent modifier le plan de recherche, que les ambiguïtés importantes provoquent une clarification utile et que les demandes suffisamment définies ne sont pas ralenties par des clarifications inutiles. Les questions simples et les documents nommés doivent emprunter un chemin proportionné à leur complexité.
La quatrième phase stabilise la recherche multiétape et la mémoire de rendement. Qwen3 reçoit un état compact des routes, requêtes, offsets, rendements, zones explorées, preuves acceptées ou rejetées et besoins encore non couverts. Il peut poursuivre, paginer, pivoter, changer d’outil, arrêter la recherche ou déclarer une insuffisance. Les répétitions mécaniquement identiques et les boucles sans rendement sont empêchées sans imposer une décision sémantique au modèle.
La cinquième phase garantit l’intégrité verticale des preuves. Une preuve trouvée ne doit plus pouvoir disparaître ou perdre son identité entre retrieval, normalisation, sélection, writer, vérification et interface. Les réponses, citations et cartes source doivent toutes dériver du même EvidenceBundle. Les réparations éventuelles sont ciblées sur les éléments refusés et ne régénèrent pas arbitrairement les éléments déjà correctement prouvés.
La sixième phase valide les comportements fonctionnels généraux : questions factuelles simples, documents explicitement nommés, navigation dans des documents longs, demandes nécessitant plusieurs sources, ambiguïtés, hors corpus, suivis conversationnels, non-répétition, autres catégories et autres langues. Elle valide aussi les contrats des différentes mémoires, leur isolation entre utilisateurs, tenants et projets, leur compaction et leur comportement sous pression de contexte.
La septième phase exécute les validations live end-to-end et les benchmarks de performance. Sur la machine P520, les objectifs sont une médiane inférieure ou égale à 30 secondes pour une question simple, 45 secondes pour un document nommé et sept minutes pour le planning complexe, sans run de planning supérieur à dix minutes. Aucun dépassement de contexte ou crash mémoire n’est accepté. Toute cible non atteinte doit être accompagnée d’une mesure du goulot réel et de la meilleure variante comparée, sans compensation aveugle par davantage de contexte, de tours, de timeout ou de prompts.
Le planning de cinq jours × quatre repas est approuvé uniquement lorsque le corpus permet réellement vingt propositions concrètes, nommées, distinctes et adaptées à leurs créneaux, chacune associée à une preuve et à une carte source exacte. Si vingt éléments valides n’existent pas, le système doit signaler précisément l’insuffisance au lieu d’inventer ou de transformer un ingrédient, une rubrique, un sommaire ou un fragment OCR en recette. Les scénarios probabilistes critiques nécessitent trois réussites live consécutives sur un état figé.
La huitième phase consolide le produit et la dette historique. Elle identifie l’unique chemin source-backed de production, supprime uniquement les voies mortes prouvées, retire les fallbacks historiques devenus inutiles, modularise les monolithes, migre les anciennes cartes avant toute purge, conserve les modifications utilisateur sans rapport et produit des commits petits, cohérents, vérifiés et réversibles. Aucun nettoyage global du worktree n’est permis.
La neuvième phase valide le parcours WinUI réel : envoi des demandes, affichage des réponses, comportement de la mémoire, erreurs et insuffisances, rendu des sources et ouverture de chaque carte sur le bon document et la bonne page. La clôture exige des builds et suites de tests verts, git diff --check propre, aucune citation inventée, aucune preuve issue uniquement de la mémoire, aucune duplication canonique, aucun fallback historique actif, aucune règle métier spécifique aux scénarios de test et un rapport final reproductible précisant versions, configurations, commandes, métriques, artefacts, limites résiduelles et état Git.
Des rapports Telegram compréhensibles doivent être envoyés aux paliers réellement importants : approbation ou réouverture d’une phase, découverte d’une régression critique, décision architecturale majeure, résultat end-to-end significatif et véritable blocage nécessitant une action de l’utilisateur. Ces rapports doivent expliquer en langage clair ce qui a été tenté, observé, conservé, rejeté et ce qui suit. Les identifiants internes comme EXP-xxx, PRV-xxx ou DEC-xxx peuvent servir à la traçabilité, mais ne doivent jamais remplacer l’explication destinée à l’utilisateur.
Le chantier n’est terminé que lorsque l’utilisateur peut poser des demandes documentaires simples ou complexes et obtenir dans SAAIA des réponses correctes, proportionnées, traçables et vérifiables, avec des sources exactes réellement ouvrables dans WinUI. Si une même classe d’échec réapparaît, arrêter l’empilement de correctifs locaux, remesurer toute la chaîne, comparer des architectures génériques et conserver uniquement la solution dont l’amélioration end-to-end est démontrée.
