# Goal — RAG canonique, LLM-first, sans dette historique

> Date de définition : 2026-07-28
> Branche de travail : `SAAIA_V3.1`
> Référence d’architecture : `ADR-2026-07-08-rag-llm-orchestration-source-backed.md`
> Corpus de validation prioritaire : les 10 PDF canoniques de `Cuisine/`
> Portée : ingestion publiée, index, retrieval, outils documentaires, orchestration
> Qwen3 côté client, mémoire, `EvidenceBundle`, citations et rendu WinUI.

## Texte court à copier dans le Goal Codex

Refondre et achever le RAG SAAIA de bout en bout afin que Qwen3 côté client
soit l’orchestrateur et le décideur sémantique principal, que le backend et le
code client ne réalisent plus que des opérations mécaniques, transparentes et
traçables, et que toute réponse simple ou complexe repose exclusivement sur
des preuves canoniques résolubles jusqu’au fichier, à la page et au passage
source. Aligner entièrement retrieval, outils, `EvidenceBundle`, mémoire,
citations et WinUI sur la nouvelle ingestion canonique; réingérer et auditer
les 10 documents Cuisine; supprimer les doublons physiques/indexés, le
pipeline source-backed historique, les heuristiques et garde-fous devenus
inutiles; modulariser les monolithes; puis démontrer par des tests
déterministes, des essais live répétés et un parcours WinUI réel que les
questions simples, les documents nommés, les suivis avec mémoire, les demandes
hors corpus et un planning de repas professionnel de 5 jours × 4 repas sont
corrects, complets, rapides, sans invention et dotés de sources cliquables
exactes. Ne pas s’arrêter sur une solution locale en échec : mesurer, prendre
du recul, comparer les variantes et changer d’architecture lorsque les
preuves indiquent que c’est préférable. Terminer avec un dépôt propre, sans
code mort, des commits cohérents et un rapport de validation reproductible.

## Résultat produit attendu

Pour une demande comme :

> J’ai besoin d’un planning de repas pour la semaine du lundi au vendredi,
> incluant petit-déjeuner, déjeuner, collation et souper.

le logiciel doit produire un tableau professionnel de 20 cellules contenant
20 propositions culinaires réelles, nommées, adaptées au créneau choisi,
non dupliquées et soutenues par le corpus. Chaque proposition doit être reliée
à une preuve canonique et à une carte source ouvrable sur le bon fichier et la
bonne page. Si le corpus ne permet pas honnêtement de remplir une partie du
tableau, le LLM doit le dire précisément au lieu d’inventer ou de remplir avec
un ingrédient, un titre de rubrique ou un fragment OCR.

La même architecture doit répondre sans surcoût disproportionné à une
question factuelle simple, retrouver un document explicitement nommé,
exploiter correctement un suivi conversationnel et refuser proprement une
demande hors corpus. Aucune règle ne doit être codée spécialement pour la
cuisine, les repas, une catégorie, un nom de document ou une langue.

## Frontières de responsabilité non négociables

### Le LLM client décide

- de l’intention sémantique de la demande;
- du plan de recherche et de sa révision;
- de l’outil utile à chaque étape, sans outil ni nombre d’appels imposé;
- des requêtes, du corpus, des documents ou pages à explorer;
- de la pertinence, de la complémentarité et de la diversité sémantiques;
- du moment où les preuves sont suffisantes ou insuffisantes;
- de l’affectation des preuves aux éléments du livrable;
- de la formulation finale et de ses limites.

### Le code décide seulement de faits mécaniques

- validation des schémas, types, bornes de sécurité et autorisations;
- isolation du tenant et résolution exacte des chemins;
- calcul des scores bruts, fusion mathématique explicitement déclarée,
  déduplication par identifiant canonique et pagination;
- respect des budgets matériels, temps, contexte et concurrence;
- intégrité, traçabilité, provenance, résolution fichier/page/chunk/ancre;
- vérification que chaque citation existe, est visible et soutient
  mécaniquement le passage cité;
- réparation de protocole, jamais remplacement d’un jugement sémantique.

La mémoire fournit du contexte, des préférences et la continuité de
conversation. Elle n’est jamais une preuve documentaire et ne peut jamais
créer une citation.

Les six mémoires actives à traiter comme un système cohérent sont :

- `M0` — Policy Memory;
- `M1-lite` — Workspace Canonical Memory;
- `M2` — Project Memory, réactivée par décision produit du 2026-07-28;
- `M3` — Session Working Memory;
- `M5` — Corpus Memory;
- `M6` — Execution / Observability Memory.

L’historique conversationnel persistant du chat-store est une archive complète,
pas une septième mémoire à injecter telle quelle. Plusieurs discussions peuvent
appartenir à un même projet. La mémoire `M2` consolide uniquement les objets
structurés utiles entre ces discussions : décisions, contraintes, préférences de
projet, requêtes déjà exécutées, sources observées/acceptées/rejetées, éléments
déjà livrés et pointeurs vers leur discussion d’origine. Cette décision remplace
explicitement, pour le chantier en cours, le report de `M2` à v4+ du CDC V3.1;
elle doit être formalisée par ADR et intégrée à la prochaine révision du CDC.

Leur audit doit couvrir le propriétaire, la source de vérité, la durée de vie,
la persistance, le budget de contexte, la compaction, la provenance, la
lecture par Qwen3 et l’écriture en fin de tour. Le LLM doit disposer d’une
mémoire de recherche structurée : requêtes déjà exécutées, zones explorées,
candidats examinés, preuves acceptées ou écartées avec leur motif, et éléments
finalement utilisés. Ces états ne doivent pas être confondus avec une preuve.

Ni l’historique complet ni toute la mémoire projet ne doivent être placés dans
la fenêtre de contexte. Qwen3 reçoit une projection compacte, typée et bornée,
puis dispose d’outils de lecture à la demande pour explorer la mémoire du projet.
Le budget est calculé avec le tokenizer réel du modèle lorsque disponible, avec
réserves séparées pour politique, outils, mémoire de session, mémoire projet,
preuves et sortie. La compaction ne doit jamais supprimer les contraintes
actives, les identités déjà utilisées ni la provenance des décisions.

Le LLM serveur reste hors du chemin conversationnel client. Dans cette
mission, il peut seulement rester disponible pour les tâches backend
d’ingestion ou d’enrichissement déjà prévues; il ne remplace pas Qwen3 client
comme orchestrateur de la réponse.

## Baseline prouvée le 2026-07-28

- Le runner `source-backed-agent-v2` est encore désactivé par défaut et dépend
  de `SAAIA_SOURCE_BACKED_AGENT_V2=1`; le client normal peut donc retomber sur
  le pipeline historique.
- Même en V2, les outils traversent encore `ExecuteToolsAsync` et les chemins
  `rag.search` / `rag.multi_search` historiques.
- Le client réécrit encore certaines requêtes, redirige certaines intentions
  et revalide sémantiquement des catégories avant l’appel backend.
- `RagEndpoints.cs` contient 36 487 lignes, environ 1 020 méthodes statiques
  et 101 méthodes `Should…`; `SearchCoreAsync` choisit, supprime, ajoute ou
  priorise de nombreuses routes selon le texte de la question.
- Un hit `rag.search` peut matérialiser toutes ses
  `MatchedContentCards` comme nouvelles preuves, ce qui gonfle le contexte et
  mélange résultat de retrieval et inventaire de cartes.
- Le live 4K est limité par le contexte; le live 8K supprime les débordements
  mais reste lent et n’obtient que 17 recettes utilisables après 16 tours.
- Avant la réingestion lancée le 2026-07-28, seuls 2 des 10 PDF canoniques
  Cuisine possédaient des `document_source_anchors` et le profil
  `deterministic_canonical_v3`; les 8 autres utilisaient encore
  `deterministic_v1`.
- Le disque serveur contient aussi 10 copies sous `Cuisine/PDF/`. Le scan
  administrateur ne réutilise pas le planificateur de doublons du scanner
  automatique et a donc mis 20 chemins en file. Les 10 jobs de copies ont été
  annulés avant ingestion; les 10 chemins canoniques sont en cours ou en file.

Cette baseline interdit de conclure qu’un nouveau prompt ou un nouveau seuil
isolé suffira.

## Plan d’exécution obligatoire

### Phase 0 — Figer les preuves et rendre le diagnostic reproductible

- Capturer branche, commit, état Git, versions client/backend/runtime/modèle.
- Archiver les traces des lives 4K et 8K, leurs réponses, temps, tokens,
  appels LLM, appels outils, candidats et motif terminal.
- Produire une cartographie d’appel réellement atteignable depuis V2 :
  router, outils, API, retrievers, fusion, cartes, `EvidenceBundle`, juge,
  writer, mémoire, résolution des sources et WinUI.
- Classer chaque règle active : mécanique nécessaire, décision sémantique à
  rendre au LLM, compatibilité temporaire ou code mort à supprimer.
- Ajouter des tests de caractérisation avant toute suppression risquée.

### Phase 1 — Terminer la migration canonique des 10 documents Cuisine

- Laisser finir ou relancer proprement les 10 jobs sur `Cuisine/<fichier>`.
- Pour chacun des 10 documents, vérifier sur la révision active :
  - statut `indexed` et job final `done`;
  - `last_ingested_at` postérieur au déploiement de l’ingestion canonique;
  - versions document/révision cohérentes;
  - présence d’ancres canoniques, de chunks et d’artefacts publiés;
  - profil actif `deterministic_canonical_v3` ou version canonique supérieure;
  - cartes paginées, titres, preuves, pages et identifiants stables;
  - points Qdrant correspondant exactement à la révision active;
  - absence de mélange entre artefacts d’une ancienne et d’une nouvelle
    révision.
- Comparer automatiquement le hash des paires `Cuisine/` et `Cuisine/PDF/`.
- Corriger `/ingestion/scan` pour qu’il applique le même
  `IngestionDuplicateFilePlanner` que le scanner de fond.
- Ajouter un test d’intégration garantissant qu’un scan manuel et un scan
  automatique choisissent le même chemin canonique.
- Après preuve d’identité et sauvegarde, supprimer les copies physiques,
  lignes documentaires, jobs résiduels, artefacts et points vectoriels
  dupliqués, sans toucher aux chemins canoniques.
- Produire un inventaire avant/après lisible pour les 10 documents.

### Phase 2 — Définir un contrat de retrieval canonique et transparent

- Séparer les primitives mécaniques :
  - recherche dense;
  - recherche lexicale/sparse;
  - recherche exacte de titre ou identifiant;
  - navigation document/sommaire/sections/pages;
  - inventaire paginé de cartes canoniques;
  - lecture de contexte autour d’une ancre;
  - fusion mathématique et déduplication canonique.
- Chaque résultat doit exposer au minimum :
  `candidateId`, `retriever`, `rawScore`, `rank`, `docId`, `revisionId`,
  `docPath`, `docName`, `pageStart`, `pageEnd`, `chunkId`, `anchorId`,
  `contentCardId`, `profileVersion`, qualité d’extraction et extrait.
- Ne plus réécrire silencieusement la requête choisie par le LLM.
- Ne plus convertir silencieusement une recherche en autre intention.
- Ne plus ajouter, retirer ou promouvoir une preuve selon une interprétation
  métier codée de la phrase utilisateur.
- Les routes mécaniques peuvent être exécutées en parallèle, mais leur liste,
  leurs scores et leurs éventuelles dégradations doivent être visibles dans la
  trace et dans le contrat remis au LLM.
- Une carte canonique ne devient une preuve atomique que lorsqu’elle est
  explicitement retournée par l’outil d’inventaire ou sélectionnée comme telle;
  un hit parent ne doit pas multiplier implicitement toutes ses cartes.
- Prévoir un mode de transition mesurable uniquement si certains documents ne
  sont pas encore canoniques. Supprimer ce mode dès que la migration du corpus
  est complète.

### Phase 3 — Rendre V2 unique et réellement piloté par Qwen3

- Activer le runner moderne par défaut, sans variable expérimentale.
- Remplacer son adaptateur vers `ExecuteToolsAsync` par un exécuteur natif
  minimal utilisant directement les nouvelles primitives.
- Conserver un catalogue d’outils descriptif et compact : aucun outil n’est
  obligatoire et aucun ordre n’est codé.
- Permettre à Qwen3 de naviguer d’abord, parcourir les cartes, rechercher,
  ouvrir du contexte, paginer, changer de requête ou terminer selon ce qu’il a
  appris.
- Conserver des budgets adaptatifs de sécurité, mais les présenter au LLM
  comme ressources restantes; ne jamais transformer un budget en plan
  sémantique.
- Séparer clairement :
  plan sémantique, observation, mémoire de travail, sélection, audit,
  rédaction et vérification mécanique.
- Réduire les appels LLM redondants. Un audit sémantique n’est relancé que si
  de nouvelles preuves ou une nouvelle décision le justifient.
- Rendre le protocole robuste aux sorties invalides par schéma contraint,
  réparation bornée et échec honnête.
- Supprimer entièrement le fallback `SourceBackedRagPipeline` historique une
  fois la parité prouvée.

### Phase 4 — Unifier `EvidenceBundle`, mémoire, citations et UI

- Faire de `EvidenceBundle` l’unique représentation des preuves depuis
  l’observation jusqu’à l’affichage.
- Stabiliser les identifiants sur l’identité canonique, jamais sur le rang ou
  la longueur d’un extrait.
- Interdire au writer de citer un identifiant non sélectionné ou non résolu.
- Vérifier chaque affirmation documentaire contre au moins une preuve
  sélectionnée; signaler précisément les affirmations non soutenues.
- Résoudre la source sur la révision active et transmettre à WinUI tous les
  champs nécessaires pour ouvrir le bon fichier à la bonne page.
- Afficher clairement fichier, page, titre d’élément et extrait utile.
- Tester les chemins Unicode, espaces, accents, pages absentes et documents
  réingérés.
- Injecter la mémoire pertinente dans l’intake avec provenance et limites;
  tracer ce qui a été utilisé; ne jamais la fusionner dans les preuves.
- Auditer les six couches de mémoire définies ci-dessus, puis supprimer les
  stockages parallèles, champs « last... » ambigus et mécanismes de
  reconstruction heuristique devenus inutiles.
- Introduire une mémoire de recherche par discussion, structurée autour
  d’identifiants canoniques stables : requête, outil, périmètre, résultat,
  décision du LLM (`accepted`, `rejected`, `used`) et motif.
- Rendre cette mémoire visible au LLM sous une forme compacte afin qu’il évite
  de refaire les mêmes recherches, sache ce qu’il lui reste à couvrir et puisse
  exclure explicitement les éléments déjà livrés lors d’une demande suivante.
- Conserver l’historique complet dans le chat-store, mais injecter dans le
  contexte une projection structurée et budgétée plutôt qu’un simple tronquage
  aveugle des derniers tours.
- Introduire la notion de projet/groupe de discussions avec isolation stricte
  `tenant + user + project`, rattachement explicite des sessions et migration
  des sessions existantes vers un projet personnel par défaut.
- Permettre de glisser-déposer une discussion commencée hors projet vers un
  projet, de la déplacer entre projets ou de la retirer d’un projet. Le
  rattachement doit être transactionnel, réversible, autorisé et ne jamais
  recopier, perdre ou réordonner ses messages.
- Créer `M2 Project Memory` comme stockage structuré versionné et dédupliqué.
  Chaque entrée doit porter `projectId`, type, clé stable, provenance
  session/message/tour, auteur de la décision, date, état
  `active|superseded|invalidated` et pointeurs source éventuels.
- Exposer au LLM un catalogue compact de mémoire projet et des outils de lecture
  paginés/recherchables. Le code borne, filtre les autorisations et classe
  mécaniquement; le LLM choisit les entrées sémantiquement utiles.
- Ne jamais injecter l’historique brut du projet par défaut. Mesurer les tokens
  réels avant chaque appel et assembler un `ContextEnvelope` avec budgets,
  ordre de priorité, éléments omis et raison de compaction traçables.
- Tester une discussion longue et plusieurs discussions du même projet : la
  latence et la qualité ne doivent pas se dégrader avec la taille totale de
  l’archive, seulement avec le paquet borné réellement injecté.

### Phase 5 — Supprimer les cadavres et modulariser

- Supprimer les fichiers, branches, prompts, adaptateurs, tests et options
  uniquement liés au pipeline V1, à Qwen2.5 ou à l’ancienne ingestion.
- Pour chaque suppression importante, prouver l’inaccessibilité ou couvrir le
  remplacement par un test.
- Extraire `RagEndpoints.cs` en modules cohérents. Aucun fichier de production
  ne doit approcher 10 000 lignes; viser moins de 1 000 lignes et n’accepter
  jusqu’à 2 000 lignes qu’avec une justification écrite.
- Éliminer les doublons de normalisation, ranking, source resolution et
  modèles de contrat entre client et backend.
- Corriger les fichiers mojibake et les commentaires obsolètes touchés.
- Maintenir `git diff --check` propre et ne pas mélanger les changements
  historiques non liés.

### Phase 6 — Optimiser le matériel et le temps de réponse

- Comparer sur la machine réelle Qwen3 Q5_K_M avec contexte 4K et 8K, KV
  `f16` et `q8_0`, en mesurant VRAM, RAM, TTFT, tok/s, durée totale,
  débordements, qualité et stabilité.
- Choisir les paramètres par profil matériel qualifié, pas par nom de
  machine : VRAM disponible, RAM, CPU, runtime, backend GPU et taille de
  contexte nécessaire.
- Ne pas augmenter le contexte pour compenser un prompt ou une observation
  gonflée; réduire d’abord la matière inutile.
- Paralléliser uniquement les recherches indépendantes choisies dans le même
  tour.
- Mettre en cache les résultats mécaniques stables et les décisions
  sémantiques réutilisables pendant le run.
- Pour la machine P520, viser :
  - question simple : médiane inférieure ou égale à 30 secondes;
  - document nommé : médiane inférieure ou égale à 45 secondes;
  - planning 5 × 4 : médiane inférieure ou égale à 7 minutes, maximum
    inférieur ou égal à 10 minutes;
  - aucun dépassement de contexte ni crash mémoire.
- Toute cible non atteinte doit être accompagnée d’un benchmark montrant le
  goulot réel et de la meilleure variante mesurée, pas d’une supposition.

### Phase 7 — Validation finale et commits

- Exécuter les tests unitaires et d’intégration backend/client.
- Ajouter une suite d’architecture interdisant le retour du fallback V1, des
  réécritures sémantiques silencieuses et des décisions métier codées.
- Exécuter au moins trois runs live consécutifs réussis pour chaque scénario
  probabiliste avant promotion.
- Tester le parcours WinUI réel, y compris le clic sur les cartes source.
- Produire un rapport final avec commandes, versions, artefacts, traces,
  métriques, échecs rencontrés, décisions, limites restantes et état Git.
- Nettoyer les artefacts temporaires non utiles.
- Découper les changements en commits cohérents et vérifiés, sans inclure les
  secrets ni les modifications utilisateur sans rapport.

## Matrice d’acceptation finale

### Corpus et ingestion

- [ ] Exactement 10 chemins canoniques Cuisine actifs et indexés.
- [ ] 10/10 révisions actives issues de l’ingestion canonique.
- [ ] 10/10 avec ancres canoniques et profils/cartes cohérents.
- [ ] 0 copie `Cuisine/PDF/` active, 0 job résiduel, 0 point Qdrant orphelin.
- [ ] Scan manuel et scanner automatique dédupliquent à l’identique.

### Architecture

- [ ] V2 est l’unique chemin source-backed de production.
- [ ] Aucun fallback V1 ou drapeau d’activation expérimental.
- [ ] Aucun exécuteur V2 ne repasse par le pipeline historique.
- [ ] Le backend n’interprète pas sémantiquement la demande pour choisir ou
  supprimer des candidats.
- [ ] `RagEndpoints.cs` est décomposé et les limites de taille sont respectées.
- [ ] Aucun code spécifique à Cuisine, repas, Q019 ou un document de test.

### Preuves et sources

- [ ] 100 % des citations retournées sont résolubles vers la révision active,
  le fichier, la page et un chunk ou une ancre.
- [ ] 0 citation fabriquée, 0 source issue uniquement de la mémoire.
- [ ] 0 duplication d’une même preuve sous plusieurs `EvidenceId`.
- [ ] Chaque carte WinUI ouvre la bonne source et affiche des métadonnées
  compréhensibles.

### Qualité fonctionnelle

- [ ] Questions simples : réponse correcte, concise et source exacte.
- [ ] Document nommé/Q019 : le nom reste ancré et la bonne source est ouverte.
- [ ] Suivi mémoire : continuité correcte sans transformer la mémoire en preuve.
- [ ] Les six couches mémoire ont chacune un contrat, une source de vérité, une
  durée de vie, un budget, une stratégie de compaction et des tests explicites.
- [ ] Plusieurs discussions peuvent être rattachées à un même projet et partagent
  `M2` sans fuite entre utilisateurs, tenants ou autres projets.
- [ ] Une discussion hors projet peut être glissée-déposée dans un projet; ses
  messages restent inchangés et sa mémoire structurée est promue de façon
  idempotente. Déplacement inter-projets, retrait et refus d’autorisation sont
  couverts par des tests backend et un parcours WinUI réel.
- [ ] Une nouvelle discussion du même projet peut retrouver à la demande les
  éléments déjà utilisés, recherches et décisions d’une discussion antérieure.
- [ ] La croissance de l’historique ou du nombre de discussions n’augmente pas
  directement le prompt : seul un `ContextEnvelope` borné est injecté.
- [ ] Sous pression 4K et 8K, les contraintes actives, identités déjà utilisées
  et provenances sont conservées; les omissions et leur motif sont tracés.
- [ ] Après un premier planning de 20 recettes, « donne-moi un autre planning »
  produit 20 autres recettes sans aucune recette déjà proposée; Qwen3 voit les
  recherches, candidats, sources acceptées/rejetées et éléments utilisés du
  premier tour, et ne répète pas inutilement une recherche déjà épuisée.
- [ ] La non-répétition repose sur les identifiants canoniques et la décision
  explicite du LLM, pas sur une liste de mots propre à la cuisine.
- [ ] Hors corpus : insuffisance honnête, sans hallucination.
- [ ] Planning 5 × 4 : 20 cellules remplies uniquement si 20 recettes valides
  existent; recettes nommées, distinctes et adaptées au service; 20 citations
  mécaniquement valides; aucun ingrédient, rubrique ou fragment OCR utilisé
  comme plat.
- [ ] Trois runs live consécutifs réussis par scénario critique.

### Performance et exploitation

- [ ] Profil P520 qualifié par mesures 4K/8K et KV `f16`/`q8_0`.
- [ ] Aucun débordement de contexte.
- [ ] Latences dans les cibles ou dérogation étayée par benchmark.
- [ ] Traces suffisantes pour expliquer toute décision, route, preuve rejetée
  et source finale sans exposer de secret.

### Qualité du dépôt

- [ ] Builds et suites de tests verts.
- [ ] `git diff --check` propre.
- [ ] Aucun modèle, option, prompt ou test Qwen2.5 obsolète.
- [ ] Aucun fichier mort conservé « au cas où ».
- [ ] Commits cohérents, rapport final reproductible et état distant vérifié.

## Règle d’arrêt

Le chantier n’est terminé que lorsque tous les critères ci-dessus sont
prouvés par des artefacts vérifiables. Un test déterministe vert ne remplace
pas un live, un live réussi ne remplace pas un parcours WinUI, et une réponse
visuellement complète ne remplace pas la validation sémantique et mécanique de
chaque source. Si une même classe d’échec réapparaît, arrêter les patchs
locaux, remesurer la chaîne complète et reconsidérer l’architecture.
