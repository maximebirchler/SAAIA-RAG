# SAAIA — Backend TODO — Document de suivi vivant

> **Derniere mise a jour :** 2026-04-20
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

## Etat global au 2026-04-20 (issu de l'audit)

| Bloc | Etat | Note |
|---|---|---|
| Architecture globale | ✅ Solide | |
| Catalogue / inventaire / endpoints | ✅ Solide | Toutes surfaces mappees |
| Pipeline retrieval | ✅ Excellent | Exact → BM25 → Dense → RRF → Calibration → Rerank → Linked |
| Evidence pack `/rag/search` | ✅ Bon | Deux gaps ouverts (voir Sprint 1) |
| Cache HTTP ETag/304 | ✅ Solide | tree + snapshot complets ; 3 endpoints catalog restants |
| Chat store | ✅ Implemente | 12 routes, migrations 005-008 |
| Admin keys / audit | ✅ Implemente | Attention : rotate sans transaction (voir P2) |
| Observabilite retrieval + governance | ✅ Solide | Spans, metriques, telemetrie |
| Gouvernance runtime | ✅ Fonctionnel | Inegale core vs A vs B |
| Capacite A | ~ v1 solide | Enrichissement deterministe reel, sans LLM |
| Capacite B | ~ v1 gouvernee | Worker reel, routing gouverne, generation non-LLM |
| Capacite C | N/A | Absente par design |
| Tests | ✅ 477 passent | 23 fichiers ; 3 surfaces sans test (voir P2) |
| Migrations SQL | ~ 28 fichiers | ⚠️ Doublons 004 et 008 (voir P1) |
| `RuntimeGovernanceService` | ⚠️ 6 185 lignes | Dette principale — refactor avant tout ajout LLM |

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
- [ ] Evaluer si les offsets PDF caractere sont calculables dans le pipeline d'extraction actuel (`Pdf/`)
- [ ] Soit implementer le peuplement de `RagItemProvenanceDto.OffsetStart` / `OffsetEnd`, soit formaliser comme TODO contractuel v3.0.1 dans le CDC
- **Fichiers concernes :** `Pdf/`, `RagEndpoints.cs`, `RagSearchDto.cs`

### 1.2 hypQuestionsMatched non branche
- [x] Brancher `hypQuestionsMatched` dans la reponse `/rag/search` via `RagItemDto` et son pipeline de mapping
- [ ] Verifier que la population se fait seulement quand les questions hypothetique A existent reellement pour l’item
- **Fichiers concernes :** `RagEndpoints.cs`, `RagSearchDto.cs`, `RuntimeGovernanceService.cs`

### 1.3 Tests Sprint 1
- [x] Ajouter un test contractuel sur la presence / absence de `hypQuestionsMatched`
- [ ] Ajouter ou ajuster un test sur les offsets si implementes
- [ ] Si offsets non implementes : ajouter / garder un test explicite de stub propre (`null`) jusqu’a implementation

---

## Sprint 2 — ETag coherence (environ 2h)

### 2.1 ETag/304 manquants sur 3 endpoints catalog
- [ ] `GET /catalog/categories` — ajouter ETag calcule + If-None-Match → 304
- [ ] `GET /catalog/documents` — ajouter ETag calcule + If-None-Match → 304
- [ ] `GET /documents/stats` — ajouter ETag calcule + If-None-Match → 304
- **Modele a suivre :** `/documents/tree` (`DocumentsEndpoints.TreeSupport.cs`) et `/catalog/snapshot` (`DocumentsEndpoints.cs`)

### 2.2 Visibilite contractuelle de `/catalog/snapshot`
- [ ] Decider si `/catalog/snapshot` reste publiquement expose par compatibilite runtime ou redevient admin-only conformement au CDC §7.5
- [ ] Si maintien public : documenter explicitement l’ecart dans le CDC / audit / docs runtime
- [ ] Si retour admin-only : prevoir alias / migration coordonnee cote clients
- **Risque :** drift doc/runtime persistant sur une surface catalogue structurante

### 2.3 Tests Sprint 2
- [ ] Ajouter tests contractuels ETag/304 pour `catalog/categories`
- [ ] Ajouter tests contractuels ETag/304 pour `catalog/documents`
- [ ] Ajouter tests contractuels ETag/304 pour `documents/stats`

---

## Sprint 3 — Dette architecturale (environ 20h) ← AVANT tout ajout LLM

### 3.1 Refactoring RuntimeGovernanceService (6 185 lignes)
- [ ] Creer `RuntimeCatalogBuilder` — artefacts catalog/model/warmup profiles
- [ ] Creer `CapabilityQualificationService` — `RequalifyAsync()`, `ReconcileStaleAsync()`
- [ ] Creer `CapabilityStateRepository` — toutes les requetes DB sur `runtime_capability_state`
- [ ] Creer `CapabilityAOrchestrator` — candidats, campagnes, enqueue A
- [ ] Creer `CapabilityBOrchestrator` — candidats, campagnes, claim/complete/fail B
- [ ] Creer `RuntimeDiagnosticsService` — diagnostics, events, recommendations
- [ ] Isoler les helpers de mapping / serialisation / artefacts dans des composants dedies
- [ ] Conserver les contrats HTTP et JSON inchanges pendant le refactor
- **Fichier source :** `RuntimeGovernance/RuntimeGovernanceService.cs` (6 185 lignes)

### 3.2 Validation Sprint 3
- [ ] Verifier que tous les tests backend passent apres refactor
- [ ] Verifier qu’aucun artefact runtime JSON ne change involontairement
- [ ] Verifier que les endpoints admin runtime gardent les memes contrats
- [ ] Rejouer les tests telemetry runtime governance
- [ ] Verifier que les routes A/B conservent le meme comportement fonctionnel

---

## Sprint 4 — Capacite A LLM (environ 20-25h)

### 4.1 Remplacer l'enrichissement deterministe par un appel LLM
- [ ] Remplacer `BuildCapabilityAHypotheticalQuestions()` par un appel LLM reel (questions hypothetiques HyPE)
- [ ] Evaluer si `BuildCapabilityASuggestedTags()` doit aussi passer en LLM ou rester deterministe
- [ ] Ajouter scoring qualite par chunk (signal de confiance, densite semantique)
- [ ] Garder un fallback deterministe si le runtime A est indisponible mais la capacite reste installee

### 4.2 Qualification Capacite A
- [ ] Etendre les hard gates du warmup a la Capacite A (sur le meme modele que `core.retrieval`)
- [ ] Definir les warmup checks minimaux de A (connectivite runtime, temps max, passCount, erreurs qualifiantes)
- [ ] Verifier que A ne casse jamais l’ingestion si elle est absente ou non qualifiee

### 4.3 Tests Capacite A
- [ ] Ajouter tests unitaires / integration sur la generation LLM des hypothetical questions
- [ ] Ajouter tests de non-regression si le runtime A est indisponible
- [ ] Verifier que l’absence de Capacite A ne casse jamais le retrieval coeur
- [ ] Verifier que `hypQuestionsMatched` est bien peuple dans `/rag/search` quand applicable
- [ ] Mesurer l’impact sur recall / precision via le harness retrieval (v5 + v6)

### 4.4 Fichiers concernes
- **Fichiers sources :** `RuntimeGovernanceService.cs` ou ses composants refactores, artefacts/runtime, warmup profiles, tests governance / retrieval

---

## Sprint 5 — Capacite B LLM (environ 12-15h)

### 5.1 Remplacer BuildDeterministicSummaryAsync par un appel LLM reel
- [ ] Remplacer `BuildDeterministicSummaryAsync()` dans `CapabilityBBackofficeWorker.cs` par un appel a un endpoint LLM configure
- [ ] Conserver le mode deterministe comme fallback explicite ou le retirer si la strategie produit l’exige
- [ ] Adapter les warmup profiles de B pour qualifier la connexion au runtime LLM
- [ ] Implementer le fallback `server_backoffice → client_admin` si le LLM est indisponible
- [ ] Mesurer TTFT et qualite des syntheses

### 5.2 Qualification Capacite B
- [ ] Etendre les hard gates du warmup a la Capacite B (connexion LLM comme pre-requis)
- [ ] Definir les checks de qualif B : connectivite runtime, timeouts, passCount, erreurs terminales
- [ ] Verifier qu’une B non qualifiee n’empeche jamais le mode `client_admin`

### 5.3 Tests Capacite B
- [ ] Ajouter tests sur le routing `client_admin` → `server_backoffice`
- [ ] Ajouter test du fallback `server_backoffice → client_admin` si le runtime LLM B est indisponible
- [ ] Ajouter test de qualification warmup profile B
- [ ] Ajouter test de persistance et completion des jobs `capability_b` en mode LLM
- [ ] Ajouter test sur les metadonnees de jobs si le mode d’execution change

### 5.4 Fichiers concernes
- **Fichiers sources :** `Ingestion/CapabilityBBackofficeWorker.cs`, gouvernance runtime B, warmup profiles, tests `SummaryBackofficeGovernanceTests.cs` et derives

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
- [ ] Wrapper `RotateKeyAsync()` dans une transaction PostgreSQL (`BeginTransactionAsync`)
- [ ] Passer `transaction: tx` aux deux `CommandDefinition` (revoke + insert)
- [ ] Ajouter un test du cas de defaillance entre les deux operations
- **Fichier source :** `Endpoints/AdminKeysEndpoints.cs`
- **Risque :** si crash entre revoke et create, ancienne cle revoquee et nouvelle absente

### Surfaces sans tests automatises
- [ ] `ChatStoreEndpoints.cs` — 12 routes (sessions, messages, tracking, aliases CDC v2.7)
- [ ] `AdminKeysEndpoints.cs` — 4 routes ; priorite sur le cas de rotation
- [ ] `AdminAuditEndpoints.cs` — 2 routes

### Dualite Provenance string / ProvenanceInfo record
- [ ] Deprecier le champ `Provenance` (string, backward-compat) quand le client sera pret
- **Fichier source :** `Models/RagSearchDto.cs`

### KPI CDC non publies
- [ ] Definir des seuils CDC sur `zero_result rate` et P95 de duree retrieval
- [ ] Documenter comment exploiter les metriques en exploitation (Grafana, alertes)

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
Visible: 0% - 100%
