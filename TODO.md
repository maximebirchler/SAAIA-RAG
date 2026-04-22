# SAAIA - Backend TODO - Document de suivi vivant

> Derniere mise a jour : 2026-04-22
> Base CDC : v3.0 complet
> Branche : SAAIA_V3.0
> Reference locale : `documents/cdc/SAAIA_Backend_Audit_2026-04-22_Complet.md`
> Note : `documents/` est ignore par Git dans ce repo. Le TODO ci-dessous est donc la source suivie dans le worktree partage.

## Legende

- `[x]` = fait et verifie
- `[ ]` = a faire
- `[~]` = partiellement fait ou a surveiller
- `[N/A]` = non applicable a ce stade

## Etat global

| Bloc | Etat | Notes |
|---|---|---|
| Architecture backend | [x] Solide | Gouvernance runtime refactoree, service monolithique casse en composants dedies |
| Catalogue / endpoints documents | [x] Solide | Cache HTTP, ETag/304, surfaces principales alignees |
| Retrieval | [x] Excellent | Exact -> BM25 -> dense -> fusion -> rerank -> linked context |
| Evidence pack `/rag/search` | [x] Tres bon | `hypQuestionsMatched` et offsets derives actifs, backfill legacy branche sur A |
| Gouvernance runtime | [x] Solide | Core / A / B gouvernes, warmup, hard gates, KPI ops |
| Capacite A | [x] Close v1 | LLM local, fallback, score qualite, KPI A, vue admin dediee |
| Capacite B | [x] Close v1 | Worker LLM local, fallback, KPI B, revue qualite corrective |
| Capacite C | [N/A] | Hors perimetre, absente par design |
| Tests backend | [x] Verts | 332 tests, 28 fichiers `*Tests.cs`, 1 fixture partagee |
| Migrations SQL | [~] Stables | 28 fichiers, doublons legacy `004` / `008` documentes et testes comme safe |

## Decision produit

- [x] La cible retenue reste `CDC v3.0 complet`
- [x] Le scenario `v3.0-lite` est abandonne
- [x] A et B restent gouvernees avec runtime LLM serveur local + fallback explicite

## Derniere validation confirmee

- [x] `dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj -p:NuGetAudit=false -nologo -m:1`
- [x] Suite backend verte : `332 / 332`
- [x] `git diff --check` sans nouvelle erreur bloquante
- [x] Warnings CRLF restants connus sur quelques fichiers deja presents dans le repo

## Priorites ouvertes

### P1 - Baseline artefacts runtime versionnee

- [ ] Ajouter une baseline versionnee des artefacts runtime/admin hors du dossier `documents/` ignore
- [ ] Normaliser les champs volatils (`generatedAt`, ids, timestamps eventuels) pour rendre cette baseline stable
- [ ] Ajouter un moyen simple de regen ou verifier cette baseline dans les tests

### P2 - Maintenance SQL legacy

- [ ] Renommer les migrations legacy `004` / `008` dans une fenetre de maintenance si on veut retrouver une sequence plus lisible
- [x] Le runner est deja verrouille pour autoriser seulement les doublons legacy connus

### P2 - Vigilance architecture warmup

- [~] Surveiller la redensification de `RuntimeCoreRetrievalWarmupEvaluator`
- [ ] Extraire une couche dediee si de nouvelles logiques LLM ou hardware y arrivent

## Validation obligatoire apres chaque sprint

- [ ] `dotnet build backend/SAAIA.Backend/SAAIA.Backend.csproj`
- [ ] `dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj -p:NuGetAudit=false`
- [ ] Si retrieval change : rejouer les regressions retrieval / harness concernees
- [ ] Si un endpoint ou un artefact change : mettre a jour les tests contractuels associes
- [ ] Mettre a jour ce `TODO.md`
- [ ] Si la note CDC locale change : realigner aussi l'audit date dans `documents/cdc/`

## Sprints clos

### Sprint 1 - Quick wins evidence pack

- [x] Offsets derives `offsetStart` / `offsetEnd`
- [x] `hypQuestionsMatched` branche et verrouille par tests explicites
- [x] Backfill legacy route via Cap A

### Sprint 2 - Coherence cache HTTP

- [x] ETag/304 ajoutes sur `catalog/categories`
- [x] ETag/304 ajoutes sur `catalog/documents`
- [x] ETag/304 ajoutes sur `documents/stats`
- [x] Statut produit de `/catalog/snapshot` tranche et documente

### Sprint 3 - Dette architecturale runtime

- [x] Refactor du monolithe `RuntimeGovernanceService`
- [x] Services read / command / gate / diagnostics extraits
- [x] Contrats top-level JSON verrouilles sur endpoints et artefacts runtime
- [x] Validation Sprint 3.2 etendue aux flux A/B operationnels et KPI A

### Sprint 4 - Capacite A

- [x] Questions hypothetiques LLM + fallback deterministe
- [x] Tags suggeres LLM + fallback deterministe
- [x] Scoring qualite A
- [x] Warmup A, KPI A, pilotage client dedie
- [x] Vue admin `KPI A` avec live ops et dry-run offsets

### Sprint 5 - Capacite B

- [x] Generation LLM locale cote worker
- [x] Fallback deterministe trace
- [x] Fallback live `server_backoffice -> client_admin`
- [x] Probe warmup / runtime B
- [x] KPI B, telemetrie, score qualite
- [x] Revue qualite corrective et relance ciblee

## Gaps fermes pendant cette session

- [x] `hypQuestionsMatched` verrouille par deux tests explicites
- [x] Vue `KPI A` rendue resiliente si `operational-summary` echoue
- [x] Labels EN restants retires de la zone `KPI A`
- [x] Contrats JSON top-level renforces sur les flux runtime A/B
- [x] Wire LLM durci :
  - `LocalLlmChatClient` couvre maintenant `content[]`, `choices` manquants, `content` manquant, corps vide, JSON invalide
  - les services A/B couvrent mieux les payloads code-fenced ou invalides
  - un test catalogue runtime fragile a ete rendu robuste aux bascules de `BACKOFFICE_LLM_ENABLED`

## Historique recent

| Date | Intervenant | Action |
|---|---|---|
| 2026-04-22 | Codex | Cap A ops : endpoint/artifact `capability-a-kpis` + tests contractuels runtime |
| 2026-04-22 | Codex | Client admin : vue fille `KPI A`, entree runtime, entree jobs A, live ops et dry-run offsets |
| 2026-04-22 | Codex | Fermeture P3 : tests explicites `hypQuestionsMatched` et degradation partielle de `KPI A` |
| 2026-04-22 | Codex | Polish P3 : labels admin localises dans `KPI A` et regle documentaire clarifiee |
| 2026-04-22 | Codex | Sprint 3.2 formel renforce : baseline top-level JSON etendue aux flux runtime A/B operationnels |
| 2026-04-22 | Codex | Wire LLM durci : nouveaux tests bas niveau `LocalLlmChatClient`, tests A/B renforces, suite backend a 332 tests verts |

## Prochaine etape recommandee

La suite la plus logique est de versionner une baseline d'artefacts runtime/admin hors de `documents/`, afin d'avoir une preuve CDC stable et partageable sans reintroduire de dependance a un dossier ignore par Git.
