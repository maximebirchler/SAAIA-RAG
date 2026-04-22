# SAAIA — Backend TODO — Document de suivi vivant

> **Derniere mise a jour :** 2026-04-22
> **Base CDC :** v3.0 (2026-04-10)
> **Branch :** SAAIA_V3.0
> **Auteurs :** Maxime Birchler, Claude, ChatGPT/Codex
>
> Ce document est derive de l'audit canonique backend v3.0 (voir `documents/cdc/SAAIA_Backend_Audit_CDC_v3.0_Complet.md`).
> Chaque intervenant met a jour ce fichier apres chaque patch (cocher, noter la date, le fichier modifie).

---

## Legende

- `[x]` = fait et verifie
- `[ ]` = a faire
- `[~]` = partiellement fait ou en cours
- `[N/A]` = non applicable a ce stade

---

## Etat global au 2026-04-21 (issu de l'audit)

| Bloc | Etat | Note |
|---|---|---|
| Architecture globale | ✅ Solide | |
| Catalogue / inventaire / endpoints | ✅ Solide | Toutes surfaces mappees |
| Pipeline retrieval | ✅ Excellent | Exact → BM25 → Dense → RRF → Calibration → Rerank → Linked |
| Evidence pack `/rag/search` | ✅ Tres bon | `hypQuestionsMatched` + offsets derives implementes ; backfill legacy branche via Cap A |
| Cache HTTP ETag/304 | ✅ Solide | tree + snapshot + endpoints catalog complets |
| Chat store | ✅ Implemente | 12 routes, migrations 005-008 |
| Admin keys / audit | ✅ Implemente | Attention : rotate sans transaction (voir P2) |
| Observabilite retrieval + governance | ✅ Solide | Spans, metriques, telemetrie |
| Gouvernance runtime | ✅ Fonctionnel | Inegale core vs A vs B |
| Capacite A | ~ v1 solide | Enrichissement deterministe reel, sans LLM |
| Capacite B | ✅ v1 LLM gouvernee close | Worker reel, routing gouverne, generation LLM locale, fallback, KPI et revue qualite corrective |
| Capacite C | N/A | Absente par design |
| Tests | 297 backend passent | Baseline runtime + surfaces backend critiques couvertes |
| Migrations SQL | ~ 28 fichiers | ⚠️ Doublons 004 et 008 (voir P1) |
| `RuntimeGovernanceService` | ✅ 112 lignes non vides | Ancien monolithe reduit a des helpers transverses ; commandes et lectures extraites |

---

## Decision produit / CDC a figer avant Sprint 4

- [ ] Arbitrer officiellement si la cible reste `CDC v3.0 complet` (A et B avec moteur LLM serveur) ou `CDC v3.0-lite` (A/B gouvernees deterministes)
- [ ] Si `v3.0-lite` retenu : mettre a jour explicitement le CDC, l’audit canonique et les docs runtime pour eviter un faux gap structurel permanent
- [ ] Si `v3.0 complet` retenu : conserver les Sprints 4 et 5 comme cibles normatives
- [ ] Tracer la decision dans le CDC et dans l’audit canonique backend

---

## Validation obligatoire apres chaque sprint

- [ ] `dotnet build backend/SAAIA.Backend/SAAIA.Backend.csproj`
- [ ] `dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj -p:NuGetAudit=false`
- [ ] Si retrieval touche : lancer les regressions retrieval / harness concernes
- [ ] Si endpoints touches : ajouter ou mettre a jour les tests contractuels correspondants
- [ ] Si contrat / note / priorite changent : mettre a jour `documents/cdc/SAAIA_Backend_Audit_CDC_v3.0_Complet.md`
- [ ] Mettre a jour ce `TODO.md` (cases cochees, date, historique)
- [ ] Si migrations SQL ajoutees : verifier l’ordre, l’unicite des numeros et le comportement du runner

---

## Sprint 1 — P0 + quick wins (environ 4-6h)

### 1.1 offsetStart / offsetEnd non peuples
- [x] Evaluer si les offsets PDF caractere sont calculables dans le pipeline d'extraction actuel (`Pdf/`)
- [x] Implementer le peuplement derive de `RagItemProvenanceDto.OffsetStart` / `OffsetEnd` via les `metadata` des units/chunks/exact entries
- [x] Documenter explicitement la base choisie : offsets derives sur le texte documentaire normalise, avec reindex requis pour backfill des documents deja indexes
- [x] Faire remonter les documents indexes legacy sans offsets comme candidats Cap A pour reindex controle
- **Fichiers concernes :** `Pdf/`, `RagEndpoints.cs`, `RagSearchDto.cs`

### 1.2 hypQuestionsMatched non branche
- [x] Brancher `hypQuestionsMatched` dans la reponse `/rag/search` via `RagItemDto` et son pipeline de mapping
- [x] Verifier que la population se fait seulement quand les questions hypothetique A existent reellement pour l’item — satisfait : `ResolveHypQuestionsMatched()` retourne `null` si `hypotheticalQuestions.Count == 0`
- **Fichiers concernes :** `RagEndpoints.cs`, `RagSearchDto.cs`, `RuntimeGovernanceService.cs`

### 1.3 Tests Sprint 1
- [x] Ajouter un test contractuel sur la presence / absence de `hypQuestionsMatched`
- [x] Ajouter ou ajuster un test sur les offsets si implementes
- [x] Remplacer le stub `null` par des assertions de presence / coherence quand l’indexation produit les offsets

---

## Sprint 2 — ETag coherence (environ 2h)

### 2.1 ETag/304 manquants sur 3 endpoints catalog
- [x] `GET /catalog/categories` — ajouter ETag calcule + If-None-Match → 304
- [x] `GET /catalog/documents` — ajouter ETag calcule + If-None-Match → 304
- [x] `GET /documents/stats` — ajouter ETag calcule + If-None-Match → 304
- **Modele a suivre :** `/documents/tree` (`DocumentsEndpoints.TreeSupport.cs`) et `/catalog/snapshot` (`DocumentsEndpoints.cs`)

### 2.2 Visibilite contractuelle de `/catalog/snapshot`
- [x] Decider si `/catalog/snapshot` reste publiquement expose par compatibilite runtime ou redevient admin-only conformement au CDC §7.5
- [x] Si maintien public : documenter explicitement l’ecart dans le CDC / audit / docs runtime
- [N/A] Si retour admin-only : prevoir alias / migration coordonnee cote clients
- **Risque :** drift doc/runtime persistant sur une surface catalogue structurante

### 2.3 Tests Sprint 2
- [x] Ajouter tests contractuels ETag/304 pour `catalog/categories`
- [x] Ajouter tests contractuels ETag/304 pour `catalog/documents`
- [x] Ajouter tests contractuels ETag/304 pour `documents/stats`

---

## Sprint 3 — Dette architecturale (environ 20h) ← AVANT tout ajout LLM

### 3.1 Refactoring RuntimeGovernanceService (112 lignes non vides au 2026-04-21)
- [x] Creer `RuntimeCatalogBuilder` — artefacts catalog/model/warmup profiles
- [x] Creer `RuntimeGovernanceCatalogService` — catalog runtime + artefacts catalog/model/warmup profiles extraits
- [~] Creer `CapabilityQualificationService` — couvert aujourd'hui par `RuntimeCapabilityLifecycleCoordinator` (`RequalifyAsync()`, `ReconcileStaleAsync()`)
- [x] Creer `RuntimeCapabilityGateService` — hard gates materiels + gate `capability ready`
- [x] Creer `CapabilityStateRepository` — couvert par `RuntimeCapabilityStateStore`
- [x] Creer `CapabilityAOrchestrator` — couvert par `RuntimeCapabilityAEnrichmentCoordinator` + stores/campaigns
- [x] Creer `CapabilityBOrchestrator` — couvert par `RuntimeCapabilityBExecutionCoordinator` + stores/campaigns
- [x] Creer `RuntimeGovernanceReadService` — capabilities, diagnostics, operational summary, warmup results, events et artefacts read-only extraits
- [x] Creer `RuntimeCapabilityAdminReadService` — candidats, campagnes et artefacts A/B read-only extraits
- [x] Creer `RuntimeGovernanceCommandService` — requalify, reconcile stale et selection extraits
- [x] Creer `RuntimeCapabilityAEnrichmentCommandService` — enqueue A extrait
- [x] Creer `RuntimeCapabilityBBackofficeCommandService` — enqueue B extrait
- [x] Creer `RuntimeCapabilityBExecutionCommandService` — claim, complete, fail et completion summary B extraits
- [x] Creer `RuntimeDiagnosticsService` — service injectable ajoute ; endpoints runtime branches dessus, wrappers de compatibilite conserves
- [~] Isoler les helpers de mapping / serialisation / artefacts dans des composants dedies — `RuntimeGovernanceJson`, `RuntimeGovernanceGateModels` et helper async read-only deplace dans `RuntimeGovernanceReadService`
- [x] Conserver les contrats HTTP et JSON inchanges pendant le refactor - baseline DTO + JSON top-level ajoutee sur les endpoints et artefacts runtime
- **Fichier source :** `RuntimeGovernance/RuntimeGovernanceService.cs` (112 lignes non vides / 121 physiques) + `RuntimeGovernance/RuntimeGovernanceCommandService.cs` (108 / 117) + `RuntimeGovernance/RuntimeGovernanceCatalogService.cs` (90 / 97) + `RuntimeGovernance/RuntimeGovernanceReadService.cs` (294 / 313) + `RuntimeGovernance/RuntimeCapabilityAdminReadService.cs` (523 / 551) + `RuntimeGovernance/RuntimeCapabilityAEnrichmentCommandService.cs` (116 / 124) + `RuntimeGovernance/RuntimeCapabilityBBackofficeCommandService.cs` (204 / 223) + `RuntimeGovernance/RuntimeCapabilityBExecutionCommandService.cs` (160 / 172)

### 3.2 Validation Sprint 3
- [x] Verifier que tous les tests backend passent apres refactor
- [x] Verifier qu'aucun artefact runtime JSON ne change involontairement - baseline top-level ajoutee sur les artefacts runtime
- [x] Verifier que les endpoints admin runtime gardent les memes contrats — baseline DTO + serialization JSON ajoutee
- [x] Rejouer les tests telemetry runtime governance
- [x] Verifier que les routes A/B conservent le meme comportement fonctionnel via la suite backend

---

## Sprint 4 — Capacite A LLM (environ 20-25h)

### 4.1 Remplacer l'enrichissement deterministe par un appel LLM
- [~] Remplacer `BuildCapabilityAHypotheticalQuestions()` par un appel LLM reel (questions hypothetiques HyPE) - generation LLM locale ajoutee pour les candidats/campagnes A avec fallback deterministe ; `/rag/search` consomme aussi le service via DI quand disponible, mais le coeur legacy/fallback reste present
- [x] Evaluer si `BuildCapabilityASuggestedTags()` doit aussi passer en LLM ou rester deterministe - tags A branches sur LLM local avec fallback deterministe
- [x] Ajouter scoring qualite par chunk (signal de confiance, densite semantique) - score qualite preview A ajoute avec signaux sections/extraits/tags/questions
- [x] Garder un fallback deterministe si le runtime A est indisponible mais la capacite reste installee

### 4.2 Qualification Capacite A
- [ ] Etendre les hard gates du warmup a la Capacite A (sur le meme modele que `core.retrieval`)
- [ ] Definir les warmup checks minimaux de A (connectivite runtime, temps max, passCount, erreurs qualifiantes)
- [ ] Verifier que A ne casse jamais l’ingestion si elle est absente ou non qualifiee

### 4.3 Tests Capacite A
- [x] Ajouter tests unitaires / integration sur la generation LLM des hypothetical questions
- [ ] Ajouter tests de non-regression si le runtime A est indisponible
- [ ] Verifier que l’absence de Capacite A ne casse jamais le retrieval coeur
- [x] Verifier que `hypQuestionsMatched` est bien peuple dans `/rag/search` quand applicable
- [ ] Mesurer l’impact sur recall / precision via le harness retrieval (v5 + v6)

### 4.4 Fichiers concernes
- **Fichiers sources :** `RuntimeGovernanceService.cs` ou ses composants refactores, artefacts/runtime, warmup profiles, tests governance / retrieval

---

## Sprint 5 — Capacite B LLM (environ 12-15h)

### 5.1 Remplacer BuildDeterministicSummaryAsync par un appel LLM reel
- [x] Remplacer `BuildDeterministicSummaryAsync()` dans `CapabilityBBackofficeWorker.cs` par un appel a un endpoint LLM configure - `CapabilityBBackofficeSummaryService` branche sur LLM local avec fallback explicite
- [x] Conserver le mode deterministe comme fallback explicite ou le retirer si la strategie produit l’exige - fallback deterministe conserve et trace dans `summary_meta`
- [x] Adapter les warmup profiles de B pour qualifier la connexion au runtime LLM - probe `llm.chat_completion` utilise par warmup / selection / diagnostics
- [x] Implementer le fallback `server_backoffice → client_admin` si le LLM est indisponible - fallback live `runtime_unavailable`
- [x] Mesurer TTFT et qualite des syntheses - mesure proxy `first_response_ms` + `qualityScore` / signaux qualite / revue corrective B

### 5.2 Qualification Capacite B
- [x] Etendre les hard gates du warmup a la Capacite B (connexion LLM comme pre-requis)
- [x] Definir les checks de qualif B : connectivite runtime, timeouts, passCount, erreurs terminales
- [x] Verifier qu’une B non qualifiee n’empeche jamais le mode `client_admin`

### 5.3 Tests Capacite B
- [x] Ajouter tests sur le routing `client_admin` → `server_backoffice`
- [x] Ajouter test du fallback `server_backoffice → client_admin` si le runtime LLM B est indisponible
- [x] Ajouter test de qualification warmup profile B
- [x] Ajouter test de persistance et completion des jobs `capability_b` en mode LLM
- [x] Ajouter test sur les metadonnees de jobs si le mode d’execution change

### 5.4 Fichiers concernes
- **Fichiers sources :** `Ingestion/CapabilityBBackofficeWorker.cs`, gouvernance runtime B, warmup profiles, tests `SummaryBackofficeGovernanceTests.cs` et derives
- **Etat reel au 2026-04-21 :** generation LLM B active avec fallback deterministe, warmup B base sur un probe `llm.chat_completion`, fallback live `server_backoffice -> client_admin` en place, et tests backend verts sur routing, warmup et completion des jobs. Les items encore reellement ouverts sur B sont surtout la mesure TTFT/qualite et un test explicite sur les metadonnees quand le mode d'execution change.
- **Mise a jour 2026-04-21 (suite) :** le test explicite sur les metadonnees de job quand le mode bascule vers `client_admin` avec statut `runtime_unavailable` est maintenant couvert ; le reliquat realiste de Sprint 5 B se concentre surtout sur TTFT/qualite et l'exploitation de ces mesures.
- **Cloture 2026-04-22 :** Sprint 5 B est considere clos cote backend/client pour le perimetre v3.0 actuel. La generation LLM locale, le fallback deterministe trace, le fallback live `server_backoffice -> client_admin`, le warmup/probe LLM, les KPI B, la telemetrie de generation, le score qualite, la revue qualite corrective, la relance forcee ciblee et les tests dedies sont en place. Les anciens items non coches ci-dessus sont conserves comme historique d'audit initial mais ne representent plus des gaps ouverts.

---

## Gaps P1 supplementaires

### Migrations 004 et 008 doublonnees
- [ ] Identifier quel runner de migration est utilise et son comportement sur noms dupliques
- [ ] Renommer les fichiers doublons pour avoir des noms uniques par numero
  - `004_phase1_security.sql` ET `004_documents_indexed_version_and_jobs_refactor.sql`
  - `008_documents_catalog_index.sql` ET `008_chat_messages_tracking.sql`
- [ ] Verifier que l’historique de migration ne sera pas casse a l’introduction de la prochaine migration
- **Risque :** comportement indefini a la prochaine migration 027 selon le runner

### Qualification inegale core vs A vs B
- [ ] Voir Sprint 4.2 et 5.2 ci-dessus
- [ ] Harmoniser les hard gates et la profondeur de warmup

---

## Gaps P2 — Dette technique (backlog)

### RuntimeGovernanceService
- [ ] Voir Sprint 3 ci-dessus

### RotateAsync sans transaction explicite
- [x] Wrapper RotateKeyAsync() dans une transaction PostgreSQL (BeginTransactionAsync)
- [x] Passer transaction: tx aux deux CommandDefinition (revoke + insert)
- [x] Ajouter un test du cas de defaillance entre les deux operations
- **Fichier source :** `Endpoints/AdminKeysEndpoints.cs`
- **Risque :** si crash entre revoke et create, ancienne cle revoquee et nouvelle absente

### Surfaces sans tests automatises
- [x] ChatStoreEndpoints.cs - sessions, messages, alias CDC et tracking live/fallback couverts
- [x] AdminKeysEndpoints.cs - 4 routes ; create/list/rotate et rollback de rotation couverts
- [x] AdminAuditEndpoints.cs - 2 routes ; list/get couverts via les evenements de cles admin

### Dualite Provenance string / ProvenanceInfo record
- [x] Deprecier le champ `Provenance` (string, backward-compat) - champ marque obsolete + EditorBrowsable(Never), `ProvenanceInfo` devient la forme canonique
- **Fichier source :** `Models/RagSearchDto.cs`

### KPI CDC non publies
- [x] Definir des seuils CDC sur `zero_result rate` et P95 de duree retrieval - endpoint/artifact `retrieval-kpis` ajoute avec cibles CDC v3.0
- [x] Documenter comment exploiter les metriques en exploitation (Grafana, alertes) - guide OTel/Grafana/alerting publie via `retrieval-kpis`
- [x] Publier un socle KPI dedie pour la capacite B - endpoint/artifact `capability-b-kpis` ajoute avec cibles generation/fallback/failure et panneaux ops recommandes

---

## Items hors perimetre actuel (CDC explicitement reporte)

- `[N/A]` **GraphRAG / LazyGraphRAG** — Reporte (CDC §11.9, §17.7)
- `[N/A]` **HyDE** — Fallback conditionnel seulement, pas dans le hot path (CDC §11.8, §17.5)
- `[N/A]` **M2 / M4 memoires** — Reportees a v4+ (CDC §17.2)
- `[N/A]` **Multi-runtime LLM parallele** — Rejete definitivement (CDC §17.4)
- `[N/A]` **SSO / AD** — Hors perimetre v3.0 (CDC §2.2)
- `[N/A]` **Redesign global endpoints** — Interdit (CDC API-001)
- `[N/A]` **Capacite C (RetrievalIntelligence)** — Absente par design, ne pas toucher avant A et B matures

---

## Historique des interventions

| Date | Intervenant | Action |
|---|---|---|
| 2026-04-14 | Claude | Creation TODO.md initial. Verification Phase 0, ecarts retrieval/DTO/gouvernance/evaluation |
| 2026-04-14 | Claude | Evidence pack DTO : snippet, categoryPath, rerankScore, hasTable, hasWarning, contextualSnippet, provenance offsets, metrics. Autocut. Diversite section. 358 tests. |
| 2026-04-14 | Claude | maxPerDoc CDC. RetrievalCdcV3AlignmentTests.cs. 378 tests (139 backend + 239 client). |
| 2026-04-16 | Codex | BM25 sparse, fusion RRF, TEI rerank integres. |
| 2026-04-19 | Codex/Claude | Gouvernance runtime (migrations 025-026), AdminRuntimeEndpoints (33 routes), Capacite A/B orchestration, telemetrie governance. |
| 2026-04-20 | Claude | Corpus v6 (familles CDC §15.4 — 2/3/4). 4 tests harness v6. 477 tests. |
| 2026-04-20 | Claude + ChatGPT | Audit canonique backend v3.0 complet. Rapport fige : `documents/cdc/SAAIA_Backend_Audit_CDC_v3.0_Complet.md`. TODO.md reecrit depuis l'audit. |
| 2026-04-20 | ChatGPT | TODO enrichi : arbitrage `/catalog/snapshot`, decision produit v3.0 complet vs lite, validation obligatoire par sprint, sous-items de tests explicites pour A/B. |
| 2026-04-21 | Codex | Sprint 2 cache HTTP : ETag/304 ajoutes sur `/catalog/categories`, `/catalog/documents`, `/documents/stats` et `/catalog/stats` + tests contractuels d'integration. |
| 2026-04-21 | Codex | Decision snapshot : `/catalog/snapshot` reste public read-only pour le contexte runtime client ; operations de statut/refresh reservees a `/admin/catalog/*`. |
| 2026-04-21 | Codex | Sprint 3 refactor runtime : etat TODO realigne sur le service a 1 656 lignes ; helper `ExecuteArtifactReadAsync` ajoute pour centraliser la telemetrie async des artefacts. |
| 2026-04-21 | Codex | Sprint 3 refactor runtime : `RuntimeCapabilityGateService` extrait (hard gates + gate capability ready) ; service principal a 1 598 lignes. |
| 2026-04-21 | Codex | Sprint 3 refactor runtime : `RuntimeGovernanceGateModels` extrait ; le warmup evaluator ne depend plus des types imbriques du service principal, descendu a 1 555 lignes. |
| 2026-04-21 | Codex | Sprint 3 refactor runtime : `RuntimeGovernanceReadService` extrait (capabilities, diagnostics, operational summary, warmup results, events, artefacts read-only) ; service principal descendu a 1 272 lignes. |
| 2026-04-21 | Codex | Sprint 4 Cap A : `ChatOptions` + client LLM local + `CapabilityAHypotheticalQuestionService` ajoutes ; generation des questions hypothetique A branchee sur LLM avec fallback deterministe et tests dedies. |
| 2026-04-21 | Codex | Sprint 4 Cap A : `/rag/search` branche le recalcul `hypQuestionsMatched` sur `CapabilityAHypotheticalQuestionService` via `RequestServices` quand present ; test d'integration ajoute sur la voie DI/LLM. |
| 2026-04-21 | Codex | Sprint 3 refactor runtime : `RuntimeCapabilityAdminReadService` extrait (candidats/campagnes/artefacts A/B read-only) ; service principal descendu a 760 lignes non vides. |
| 2026-04-21 | Codex | Sprint 3 refactor runtime : `RuntimeGovernanceCatalogService` extrait (catalog runtime + artefacts catalog/model/warmup profiles) ; service principal descendu a 679 lignes non vides. |
| 2026-04-21 | Codex | Sprint 3 refactor runtime : `RuntimeGovernanceCommandService` extrait (requalify, reconcile stale, selection) ; service principal descendu a 580 lignes non vides. |
| 2026-04-21 | Codex | Sprint 3 refactor runtime : commandes A/B extraites (`RuntimeCapabilityAEnrichmentCommandService`, `RuntimeCapabilityBBackofficeCommandService`, `RuntimeCapabilityBExecutionCommandService`) ; service principal descendu a 112 lignes non vides. |
| 2026-04-21 | Codex | Sprint 3.2 : baseline de contrat runtime ajoutee dans `AdminRuntimeEndpointsTests` (DTO + payloads JSON top-level) ; 287 tests backend verts, Sprint 3 clos. |
| 2026-04-21 | Codex | Rotation de cles admin transactionnalisee (RotateKeyAsync) + couverture AdminKeys/AdminAudit ; rollback sur echec d'audit verifie, suite backend a 290 tests verts. |
| 2026-04-21 | Codex | Couverture `ChatStoreEndpoints` ajoutee (sessions, messages, alias CDC, tracking live et fallback) ; suite backend a 294 tests verts. |
| 2026-04-21 | Codex | `RuntimeDiagnosticsService` injectable ajoute et branche sur les endpoints admin runtime ; wrappers de compatibilite conserves, suite backend toujours verte. |
| 2026-04-21 | Codex | KPI retrieval CDC publies via `GET /admin/runtime/retrieval-kpis` + artefact `retrieval-kpis.json` (seuils P95/zero-result, metriques OTel, alertes, panneaux Grafana) ; suite backend a 296 tests verts. |
| 2026-04-21 | Codex | `RagItemDto.Provenance` deprecie formellement (Obsolete + EditorBrowsable(Never)) ; `ProvenanceInfo` confirme comme forme canonique, suite backend a 297 tests verts. |
| 2026-04-21 | Codex | Sprint 5 Cap B : `CapabilityBBackofficeSummaryService` ajoute avec generation LLM locale + fallback deterministe explicite ; `CapabilityBBackofficeWorker` bascule sur le service par DI quand disponible. |
| 2026-04-21 | Codex | Sprint 5 Cap B : tests dedies ajoutes pour le service de synthese B et pour la persistance/completion d'un job `capability_b` en mode LLM ; suite backend a 303 tests verts. |
| 2026-04-21 | Codex | Sprint 5 Cap B : la qualification runtime de `capability_b.backoffice_generation` ne repose plus seulement sur `BACKOFFICE_LLM_ENABLED` ; elle execute maintenant un vrai probe `llm.chat_completion` sur chaque pass de warmup avant autorisation/selection. |
| 2026-04-21 | Codex | Sprint 5 Cap B : couverture ajoutee pour la requalification B en succes et en echec de probe LLM ; suite backend a 304 tests verts. |
| 2026-04-21 | Codex | Sprint 5 Cap B : `ResolveSummaryGenerationExecutionAsync()` ajoute un probe runtime live avant de choisir `server_backoffice` ; fallback nominal `client_admin` applique avec statut `runtime_unavailable` si le runtime LLM n'est plus joignable. |
| 2026-04-21 | Codex | Sprint 5 Cap B : test de fallback live ajoute sur la decision d'execution summary B ; suite backend a 305 tests verts. |
| 2026-04-21 | Codex | Sprint 5 Cap B : probe live B factorise dans `CapabilityBLiveRuntimeProbe` et reutilise par le warmup B, la decision `summary.generate` et les diagnostics runtime admin. |
| 2026-04-21 | Codex | Sprint 5 Cap B : `diagnostics` et `operational-summary` ajoutent un blocker `runtime_live_unavailable` sur B selectionnee quand le probe live echoue ; suite backend a 306 tests verts. |
| 2026-04-21 | Codex | Sprint 5 Cap B : les artefacts `catalog` / `runtime_catalog.json` / `model_catalog.json` exposent maintenant la dependance explicite `server-capability-b -> llm-backoffice-chat`, avec base URL/modele LLM visibles et contrat admin runtime plus litteral cote CDC ; suite backend toujours verte a 306 tests. |
| 2026-04-21 | Codex | Sprint 5 Cap B : test ajoute sur le fallback live `runtime_unavailable -> client_admin` jusqu'au job termine, avec verification des metadonnees `ExecutionMode` / `RuntimeCapabilityStatus` et absence de `summary_meta` B parasite sur la soumission admin ; suite backend a 307 tests verts. |
| 2026-04-21 | Codex | Sprint 5 Cap B : endpoint/artifact `capability-b-kpis` ajoutes, avec metriques/alertes/panneaux dedies a la generation B ; telemetrie runtime enrichie pour les decisions d'execution B et la generation de resumes (latence, fallback, longueur de sortie) ; suite backend a 309 tests verts. |
| 2026-04-21 | Codex | Sprint 5 Cap B : le client LLM local expose maintenant une mesure proxy `first_response` (premier octet lisible, pas un vrai TTFT stream token) ; cette mesure remonte dans la telemetrie/KPI B et dans les metadonnees de generation LLM quand disponibles. |
| 2026-04-21 | Codex | Sprint 5 Cap B : un score heuristique `qualityScore` est ajoute aux metadonnees de synthese B (structure, longueur, couverture des sections et mots-cles d'extraits) ; il remonte aussi dans la telemetrie/KPI B via `summary_generation.quality_score` et une alerte dediee. |
| 2026-04-21 | Codex | Sprint 5 Cap B : nouvelle surface read-only `quality-review` ajoutee pour lister uniquement les resumes B stockes sous le seuil qualite configure, avec artefact dedie et recommandations de revue, afin d'eviter de surcharger `diagnostics` ou les vues de campagne. |
| 2026-04-21 | Codex | Sprint 5 Cap B : `quality-review-summary` ajoute comme vue agregée separee (totaux, fallback, runtime indisponible, pire score, repartitions par strategie/statut) pour piloter la derive qualite sans ouvrir la liste detaillee. |
| 2026-04-21 | Codex | Client admin : le panneau runtime WinUI expose maintenant une entree dediee vers une vue fille `Revue qualite B`, avec overlay separe, agregats, liste des resumes sous seuil et bouton de bascule vers les jobs B, sans surcharger la vue runtime principale. |
| 2026-04-21 | Codex | Client admin : le centre des jobs affiche maintenant un raccourci contextuel vers la `Revue qualite B` uniquement en mode jobs B, pour garder la liste lisible tout en reliant les deux vues operationnelles. |
| 2026-04-21 | Codex | Client admin : la `Revue qualite B` devient corrective avec une action par item `Relancer B`, branchee sur l'enqueue B forcee du document puis rafraichissement de la revue. |
| 2026-04-21 | Codex | Cap B : l'enqueue force cible (`force=true` + `docId/docPath`) inclut maintenant les resumes `fresh` afin que la revue qualite puisse regenerer un resume faible mais techniquement a jour ; test backend ajoute, suite a 314 tests verts. |
| 2026-04-21 | Codex | Cap B : l'enqueue cible applique maintenant le filtre `docId/docPath` avant le `maxCandidates`, pour que `maxCandidates=1` ne rate pas un resume faible masque par un document plus recent ; test renforce. |
| 2026-04-21 | Codex | Cap B : l'enqueue cible ne borne plus le chargement avant filtrage explicite ; test renforce avec 500+ candidats plus recents pour verrouiller la relance qualite ciblee. |
| 2026-04-21 | Codex | Client admin : l'action `Relancer B` de la revue qualite affiche maintenant un retour operationnel detaille (`queued/candidates/skipped` + job court) apres enqueue force cible. |
| 2026-04-22 | Codex | Cap B : la revue qualite expose maintenant `severity` et `recommendedAction` par resume faible ; le client les affiche sans nouvelle vue et le test contractuel backend est renforce. |
| 2026-04-22 | Codex | Sprint 5 Cap B cloture cote backend/client : LLM local, fallback deterministe trace, fallback live client_admin, warmup/probe LLM, KPI, telemetrie, score qualite, revue corrective et relance ciblee sont consideres en place pour le perimetre v3.0 actuel. |
| 2026-04-22 | Codex | Sprint 4 Cap A : tags suggeres branches sur LLM local avec fallback deterministe ; score qualite preview A ajoute (sections/extraits/tags/questions) et propage aux candidats/campagnes ; tests A cibles verts. |
