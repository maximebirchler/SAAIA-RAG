# ADR — Mémoire projet et enveloppe de contexte bornée

> Statut : accepté pour implémentation
> Date : 2026-07-28
> Décision produit : permettre de regrouper plusieurs discussions dans un
> projet et d’en partager la mémoire sans saturer le contexte du LLM local.

## Contexte

Le CDC V3.1 active `M0`, `M1-lite`, `M3`, `M5` et `M6`, persiste les messages
dans le chat-store et reporte `M2/M4` à v4+. Cette organisation ne permet pas à
deux discussions distinctes travaillant sur le même sujet de partager leurs
recherches, décisions ou éléments déjà livrés. Injecter l’historique complet
de toutes les discussions serait incompatible avec les contextes 4K/8K de
Qwen3 et dégraderait à la fois latence, attention et qualité.

Les pratiques publiées sur les agents longs convergent vers une mémoire
hiérarchique externe, une récupération juste-à-temps et un contexte activement
budgété. La taille du stockage peut croître; la taille de l’invite ne doit pas
croître avec elle.

## Décision

### 1. Activer M2 comme Project Memory

Un projet est un conteneur explicite, isolé par `tenant_id + user_id`, auquel
zéro ou plusieurs discussions sont rattachées. Les sessions existantes sont
migrées vers un projet personnel par défaut sans perdre leurs identifiants.

Une discussion peut rester hors projet, puis être rattachée par
glisser-déposer dans WinUI. Elle peut également être déplacée entre deux
projets ou redevenir indépendante. L’opération modifie uniquement le lien de
rattachement : elle ne duplique ni la session ni ses messages.

`M2` conserve uniquement des entrées structurées :

- contraintes et préférences explicites propres au projet;
- décisions et leur état courant;
- actions documentaires exécutées;
- sources et candidats observés, acceptés ou rejetés par le LLM;
- éléments canoniques déjà utilisés dans un livrable;
- résumés de reprise validés, lorsque cette capacité sera disponible.

Chaque entrée porte une clé stable, un type, une version de schéma, sa
provenance exacte (`project`, `session`, `message`, `turn`), son auteur
(`user`, `llm`, `mechanical`), sa date, son état
`active|superseded|invalidated` et les identifiants documentaires applicables.

Les événements mécaniquement certains — appel exécuté, preuve citée et
vérifiée, identifiant canonique utilisé — peuvent être promus sans nouvelle
interprétation. Une synthèse, un motif sémantique ou une préférence implicite
reste une décision du LLM et doit être identifiée comme telle.

### 2. L’historique brut reste une archive

Les messages complets restent persistés pour l’interface, l’audit et une
reconstruction contrôlée. Ils ne sont jamais injectés intégralement par
défaut, ni confondus avec `M2` ou `M3`.

### 3. Le LLM choisit la mémoire utile

Le LLM reçoit :

- un petit catalogue de mémoire projet : types disponibles, comptes, sujets
  récents et curseurs;
- les éléments de continuité immédiate de `M3`;
- des outils de lecture projet paginés et recherchables.

Le code applique uniquement isolation, schémas, pagination, déduplication,
classement brut et budget. Il ne décide pas qu’une ancienne décision ou source
est sémantiquement pertinente pour la nouvelle demande. Qwen3 choisit s’il doit
interroger la mémoire projet, quelles entrées lire et comment en tenir compte.

### 4. La mémoire n’est jamais une preuve

Une entrée mémoire peut orienter une recherche ou empêcher une répétition. Tout
fait documentaire destiné à la réponse doit être récupéré depuis `M5` et
matérialisé dans l’`EvidenceBundle` courant. Aucune entrée `M2/M3` n’est
citable.

### 5. Assembler une ContextEnvelope à chaque appel

Chaque appel LLM reçoit une enveloppe mesurée et traçable contenant des blocs
typés :

1. politique et contrat;
2. outils réellement disponibles;
3. continuité immédiate `M3`;
4. projection ou résultats demandés de `M2`;
5. plan/état de travail courant;
6. preuves courantes;
7. réserve de sortie.

Le comptage utilise le tokenizer réel du modèle quand le runtime l’expose. Une
estimation conservatrice n’est qu’un repli explicite. Chaque profil 4K/8K
définit un maximum par bloc, une priorité et une réserve de sortie.

La réduction se fait dans cet ordre :

1. supprimer doublons et sérialisation inutile;
2. remplacer les observations volumineuses par leurs objets structurés;
3. ne conserver que les tours récents de travail;
4. rendre les détails anciens accessibles par curseur/outils;
5. compacter les notes, sans supprimer contraintes actives, identités déjà
   utilisées ni provenance;
6. refuser honnêtement l’appel si la réserve de sortie ou les invariants ne
   tiennent plus.

## Modèle de données cible

- `projects`
  - identité, tenant, user, titre, état, timestamps;
- `project_sessions`
  - projet, session, ordre/épinglage, timestamps;
- `project_memory_entries`
  - identité, projet, type, clé stable, payload JSON versionné, provenance,
    auteur, état, timestamps, identifiants source éventuels;
- index unique actif sur `project + type + stable_key`;
- index de lecture sur `project + type + updated_at`;
- index de provenance sur `session + turn`.

Les écritures doivent être idempotentes. Une nouvelle version supersède
l’ancienne; une réingestion documentaire peut invalider les pointeurs devenus
obsolètes sans effacer l’historique d’audit.

## Critères d’acceptation

- une discussion peut être déplacée vers un projet sans perte de messages;
- le glisser-déposer WinUI gère discussion hors projet vers projet, déplacement
  inter-projets, retrait et annulation visuelle après échec;
- le rattachement backend est transactionnel, idempotent et protégé contre les
  déplacements entre tenants ou utilisateurs;
- la mémoire structurée issue de la discussion est promue ou réindexée dans le
  projet cible sans doublon; le projet source ne conserve pas d’entrée active
  dont l’unique provenance était la discussion déplacée;
- deux discussions d’un même projet voient les mêmes entrées `M2`;
- aucune entrée n’est visible depuis un autre tenant, utilisateur ou projet;
- « donne-moi un autre plan » dans la même discussion ne répète aucun
  identifiant utilisé;
- la même demande dans une nouvelle discussion du même projet retrouve ces
  identifiants via `M2`;
- une nouvelle discussion hors projet ne les reçoit pas;
- 100 discussions et un historique volumineux n’augmentent pas la taille de
  l’invite au-delà du budget configuré;
- les prompts 4K/8K conservent invariants, contraintes et réserve de sortie;
- les traces donnent le nombre de tokens par bloc, les entrées lues et les
  omissions, sans journaliser inutilement leur contenu;
- toute affirmation documentaire est toujours resourcée depuis le corpus
  courant.

## Conséquences

Cette décision remplace localement le report de `M2` indiqué au CDC V3.1. Le
CDC devra être révisé avant livraison. `M4` reste reportée : aucune mémoire
personnelle générale implicite n’est introduite par cette ADR.
