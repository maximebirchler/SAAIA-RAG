# SAAIA — Ecarts CDC v3.0 vs Code — Document de suivi vivant

> **Derniere mise a jour :** 2026-04-14 (session 2)
> **Base CDC :** v3.0 (2026-04-10)
> **Branch :** SAAIA_V3.0
> **Auteurs :** Maxime Birchler, Claude (Opus 4.6), ChatGPT/Codex
>
> Ce document est un tracker vivant. Chaque IA ou humain qui travaille sur le projet
> doit mettre a jour ce fichier apres chaque intervention (cocher, ajouter des notes, dater).

---

## Legende

- `[x]` = fait et verifie
- `[ ]` = a faire
- `[~]` = partiellement fait ou en cours
- `[N/A]` = non applicable a ce stade

---

## 0. Phase 0 — Alignement contractuel et semantique (CDC §18)

> **Statut global : FAIT**
> Verifie le 2026-04-14. Tous les points P0 de l'audit v2.9.1 ont ete corriges.

- [x] **Manifest version bumped to v3.0** — `ToolManifest.cs` utilise `"v3.0"`
- [x] **meta.repair_last -> meta.rewrite_last** — `PromptCatalog.cs` et `ToolAgentOrchestrator.cs` utilisent `meta.rewrite_last` ; "repair" accepte comme alias legacy normalise
- [x] **admin.summary.store -> admin.summary.submit** — Tool `admin.summary.submit` dans manifest, endpoint `POST /admin/summaries/submit` dans backend
- [x] **strictMode supprime** — `AppSettings.cs` migre vers `ActiveMode` (auto/standard/strict) ; lecture legacy avec nettoyage actif (`ls.Values.Remove("llm.strictMode")`)
- [x] **responseStyle -> responseFormat** — `RouterPlan.cs` et `PromptCatalog.cs` utilisent `responseFormat`
- [x] **BuildConversationManifestJson() implemente** — Filtre `access=user` uniquement pour le rail conversation libre, sans condition sur session admin
- [x] **POST /sources/resolve** — Endpoint implemente dans `SourcesEndpoints.cs`
- [x] **POST /documents/resolve-category** — Endpoint implemente dans `DocumentsEndpoints.ResolveCategory.cs`
- [x] **ETag/304 sur GET /documents/tree** — SHA256 ETag + If-None-Match + 304 dans `DocumentsEndpoints.cs`
- [x] **user-prefs.bin (UserPrefsStore)** — Implemente avec DPAPI dans `UserPrefsStore.cs`, persistance langue/style/mode
- [x] **display_order en SQL** — Table `documents_catalog_categories` avec `display_order int NOT NULL` + index

---

## 1. Pipeline Retrieval — Ecarts structurels (CDC §11)

> **Statut global : PARTIELLEMENT FAIT**
> Le pipeline exact-match -> dense -> linked fonctionne. Les briques CDC manquantes sont ci-dessous.

### 1.1 BM25 / Sparse Retrieval (CDC §11.2)
- [ ] **Implanter un retrieval sparse (BM25 ou equivalent)**
  - CDC : "BM25" est explicitement liste dans le pipeline candidate selection
  - Etat : Aucune implementation. Seuls exact-match (PostgreSQL) et dense (Qdrant) existent
  - Options : PostgreSQL `tsvector`/`tsquery` pour BM25 natif, ou Qdrant sparse vectors
  - Complexite estimee : 2-3 jours
  - Fichiers concernes : `RagEndpoints.cs` (ajout d'une etape sparse dans `SearchCoreAsync`), potentiellement nouveau `SparseBm25Client.cs`

### 1.2 Fusion RRF — Reciprocal Rank Fusion (CDC §11.2-11.3)
- [ ] **Implementer la fusion RRF pour combiner exact + dense + sparse**
  - CDC : "fusion RRF" explicitement demandee
  - Etat : Les resultats sont concatenes sequentiellement (exact d'abord, puis dense, puis linked). Pas de fusion par rang
  - Algorithme : `RRF_score(d) = sum(1 / (k + rank_i(d)))` pour chaque retriever i
  - Complexite estimee : 1-2 jours
  - Fichiers concernes : `RagEndpoints.cs` — nouvelle methode `FuseWithRRF()` entre la collecte et le ranking final

### 1.3 TEI /rerank (CDC §11.3)
- [ ] **Integrer un reranking par modele TEI (cross-encoder)**
  - CDC : "TEI /rerank" dans l'etape evidence selection
  - Etat : TEI utilise uniquement pour les embeddings (`TeiClient.cs`). Le re-ranking actuel est local avec des boosts heuristiques (+0.02, +0.01, +0.005)
  - Le service TEI deploye (docker-compose) supporte potentiellement `/rerank` — a verifier
  - Complexite estimee : 1-2 jours
  - Fichiers concernes : `TeiClient.cs` (ajout `RerankAsync()`), `RagEndpoints.cs` (appel post-fusion), `RagSearchDto.cs` (ajout `rerankScore`)

### 1.4 Autocut dynamique (CDC §11.3)
- [x] **Implementer un autocut absolu + relatif** — FAIT 2026-04-14 (Claude)
  - CDC : "autocut absolu + relatif" dans evidence selection
  - Implemente : `ApplyAutocut()` dans `RagEndpoints.cs` — detecte la plus grande chute relative (>= 15%) entre scores consecutifs, coupe apres le gap. Enforce aussi le seuil absolu `minScore` sur les non-exact-match
  - Tests : 358 tests passent (119 backend + 239 client)

### 1.5 Diversite par section (CDC §11.3)
- [x] **Max 2 chunks / section implemente** — FAIT 2026-04-14 (Claude)
  - CDC : "diversite max 3 chunks / document, max 2 chunks / section"
  - Implemente : `maxPerSection = 2` dans `AddRankedMatches()` avec `BuildSectionKey()` (SectionOrdinal ou SectionTitle fallback)
  - Tests : 358 tests passent

### 1.6 Diversite par document (CDC §11.3)
- [x] **maxPerDoc default aligne sur CDC (max 3)** — FAIT 2026-04-14 (Claude)
  - CDC : "diversite max 3 chunks / document"
  - Implemente : `Math.Min(3, Math.Max(2, topK / 2))` en balanced, `Math.Min(topK, 3)` en focused, `2` en broad
  - Overridable via `req.MaxPerDoc` ou `req.Diversity.MaxChunksPerDoc`
  - Tests : 378 tests passent (139 backend + 239 client)

---

## 2. Evidence Pack — Contrat DTO (CDC §11.6)

> **Statut global : LARGEMENT FAIT** (mise a jour 2026-04-14)
> Les champs de base + enrichis CDC v3.0 sont presents. Restent : categoryRef, provenance offsets reels, hypQuestionsMatched.

### 2.1 Champs presents et conformes
- [x] `requestId` — present dans `RagSearchResponseDto`
- [x] `query` / `queryNormalized` — present
- [x] `retrieversUsed` — present dans `RagMetricsDto`
- [x] `topK` — present
- [x] `dataHash` — present (SHA256 calcule)
- [x] `score` — present dans `RagItemDto`
- [x] `docId`, `docName`, `docPath` — presents
- [x] `pageStart`, `pageEnd` — presents
- [x] `chunkId`, `chunkIndex` — presents
- [x] `text` — present (contenu du chunk)
- [x] `sourceHash` — present
- [x] `exactMatchHit` — present
- [x] `chunkType` — present
- [x] `headingPath` — present
- [x] `metrics.tookMs`, `metrics.returned` — presents

### 2.2 Champs manquants ou non conformes
- [x] **`ttlSeconds`** — FAIT 2026-04-14 (Claude). Default = 600 dans `RagMetricsDto` et dans la construction de reponse
- [x] **`categoryPath`** — FAIT 2026-04-14 (Claude). Ajoute en champ additionnel dans `RagItemDto` (peuple avec la meme valeur que `category` pour backward compat). `category` conserve pour ne pas casser le client
- [ ] **`categoryRef`** — Reference ordinale optionnelle. Absente. Necessite resolution backend via `documents_catalog_categories.display_order`
- [x] **`snippet`** — FAIT 2026-04-14 (Claude). `BuildSnippet()` dans `RagEndpoints.cs` — tronque a 500 chars sur frontiere de phrase ou mot
- [x] **`rerankScore`** — FAIT 2026-04-14 (Claude). Champ present dans le DTO (`RagItemDto.RerankScore`), peuple a `null` en attendant l'integration TEI /rerank (§1.3)
- [x] **`provenance.offsetStart` / `provenance.offsetEnd`** — FAIT 2026-04-14 (Claude). Champs ajoutes dans `RagItemProvenanceDto`. Peuples a `null` — le calcul d'offset depend du parseur PDF et n'est pas encore implemente
- [x] **`hasTable`** — FAIT 2026-04-14 (Claude). Detection heuristique par comptage de lignes avec pipes (`DetectHasTable()`)
- [x] **`hasWarning`** — FAIT 2026-04-14 (Claude). Detection par chunkType "warning" ou mots-cles (WARNING, DANGER, CAUTION, AVERTISSEMENT, ATTENTION)
- [x] **`contextualSnippet`** — FAIT 2026-04-14 (Claude). Peuple avec `embedText` (le texte contextuel utilise pour l'embedding)
- [ ] **`hypQuestionsMatched`** — Indicateur HyPE. Absent (depend Capacite A, non prioritaire)
- [x] **`metrics.teiMs`** — FAIT 2026-04-14 (Claude). Ajoute dans `RagMetricsDto.TeiMs`, peuple depuis `resp.Timings.TeiMs`
- [x] **`metrics.qdrantMs`** — FAIT 2026-04-14 (Claude). Ajoute dans `RagMetricsDto.QdrantMs`, peuple depuis `resp.Timings.QdrantMs`
- [x] **`metrics.candidates`** — FAIT 2026-04-14 (Claude). Ajoute comme `RagMetricsDto.CandidatesEvaluated`, peuple depuis `resp.Candidates`

---

## 3. Gouvernance des modeles (CDC §9)

> **Statut global : NON COMMENCE**
> Le CDC v3.0 introduit un systeme complet de gouvernance. Rien n'existe encore dans le code.

- [ ] **model_catalog.json** — Catalogue approuve de modeles client et serveur (CDC §5.8). Non cree
- [ ] **runtime_catalog.json** — Catalogue des runtimes approuves et hard gates (CDC §5.8). Non cree
- [ ] **warmup_profiles.json** — Profils de qualification versionnes (CDC §5.8). Non cree
- [ ] **warmup_results.json** — Resultats de qualification locale (CDC §5.8). Non cree
- [ ] **capability_state.json** — Etat des capacites installees/configurees/healthy/qualifiees/autorisees/selectionnees (CDC §5.8). Non cree
- [ ] **Warmup gate logic** — Validation {runtime, modele, quantification, profil materiel} avant activation (CDC §9.6). Non implemente
- [ ] **Hard gates** — Filtrage modeles par prerequis materiel (CDC §9.3). Non implemente
- [ ] **Chaine de decision** — Installee -> Configuree -> Healthy -> Qualifiee -> Autorisee -> Selectionnee (CDC §9.7). Non implementee
- [ ] **Migration LocalLlmBootstrapper.cs** — Actuellement hardcode sur 6 modeles (Qwen Q4_0/Q4_K_S/Q4_K_M/Q6_K + Mistral Q4_K_M/Q6_K). Doit migrer vers catalogue approuve filtre par profil materiel
- [ ] **Profils client C1/C2/C3/C4** — Selection par VRAM detectee dans un catalogue, pas dans du code hardcode (CDC §9.3)
- [ ] **Diagnostics de qualification** — Exposer l'etat via `GET /admin/runtime/capabilities`, `GET /admin/runtime/catalog`, `POST /admin/runtime/requalify` (CDC §7.3)

---

## 4. Capacites serveur optionnelles A/B/C (CDC §4.4)

> **Statut global : NON COMMENCE**
> Infrastructure a creer. Le CDC est clair : aucune dependance fonctionnelle sur le coeur.

- [ ] **Abstraction Capability** — Creer un systeme de capabilities avec etats (installee/configuree/healthy/qualifiee/autorisee/selectionnee)
- [ ] **Capacite A (CorpusEnrichment)** — Contextualisation LLM des chunks, HyPE, enrichissement metadata, scoring qualite, auto-tagging. Actuellement le contextual_text est code-based (conforme si A absente)
- [ ] **Capacite B (BackofficeGeneration)** — Resumes stockes, syntheses multi-docs, exports enrichis, jobs backoffice. Le worker resume admin existe partiellement
- [ ] **Capacite C (RetrievalIntelligence)** — Reformulation, decomposition, HyDE fallback, compression contextuelle. Experimentale, desactivee par defaut
- [ ] **Politique d'activation** — "Installee != activee != selectionnee" (CDC §3.3 MOD-003/MOD-004)

---

## 5. Metriques d'evaluation contractuelles (CDC §15.3)

> **Statut global : PARTIELLEMENT FAIT**
> Le harnais de regression existe (corpus v1/v2/v3 + tests). Les metriques CDC ne sont pas mesurees.

### 5.1 Harnais existant (fonctionnel)
- [x] `retrieval_eval_corpus.v1.json` — Baseline exact lookup
- [x] `retrieval_eval_corpus.v2.json` — Dominant retriever + linked context
- [x] `retrieval_eval_corpus.v3.json` — Business-oriented expectations (5+ cas)
- [x] `RetrievalEvaluationHarnessTests.cs` — Tests recall, noise, channel ordering, business decisions
- [x] `RetrievalEvaluationCorpusTests.cs` — Validation corpus structure
- [x] `retrieval-regression-runbook.v3.md` — Runbook de campagne
- [x] `run-retrieval-regression.ps1` — Script PowerShell de campagne

### 5.2 Metriques CDC manquantes
- [ ] **Context Precision > 0.75** — Non mesuree. Necessite ground truth annote
- [ ] **Context Recall > 0.80** — Non mesuree. Necessite ground truth annote
- [ ] **Faithfulness > 0.85** — Non mesuree. Necessite evaluation end-to-end avec LLM
- [ ] **Answer Relevancy > 0.80** — Non mesuree. Necessite evaluation end-to-end avec LLM
- [ ] **Exact Match Hit Rate > 0.95 sur familles ciblees** — Partiellement (recall testee sur corpus synthetique, pas sur vrais documents)
- [ ] **Retrieval P95 < 800ms** — Non mesuree. Ajouter mesure de latence dans le harnais
- [ ] **Rerank P95 < 300ms** — N/A tant que TEI /rerank n'est pas implemente (§1.3)
- [ ] **Zero-result rate < 5%** — Non mesuree. Ajouter compteur de requetes sans resultats

### 5.3 Familles de tests obligatoires (CDC §15.4)
- [~] **Famille 1 : References exactes** — Couverte partiellement par corpus v3 exactPositiveCases
- [ ] **Famille 2 : Procedures et modes operatoires** — Non couverte avec documents reels
- [ ] **Famille 3 : Syntheses mono-document** — Non couverte
- [ ] **Famille 4 : Comparaisons multi-documents** — Non couverte

---

## 6. Structure documentaire (CDC §5.4)

> **Statut global : LARGEMENT FAIT**

- [x] `documents` — Table presente avec identite canonique, versions, compteurs, etat
- [x] `document_sections` — Hierarchie logique (heading, breadcrumb, pages)
- [x] `document_units` — Plus petite unite typee extraite
- [x] `retrieval_chunks` — Objets derives optimises pour la recherche
- [x] `retrieval_chunk_links` — Navigation prev/next/same_section
- [x] `exact_match_entries` — Index deterministe normes/codes/modeles
- [~] `document_pages` / `document_page_index` — `document_page_index` existe (revision_id, page_number, char_count, checksum, metadata). CDC mentionne `document_pages` avec ancrage page, disponibilite texte natif, usage OCR, score de parsing — champs supplementaires potentiellement manquants

---

## 7. Observabilite OTel (CDC §15.2)

> **Statut global : NON VERIFIE**
> A auditer. docker-compose.otel.yml existe.

- [ ] **Spans CDC requis** — pre_router, router_llm, context_builder, tool[N], writer_llm, critic_llm, retrieval_exact_match, retrieval_dense, retrieval_sparse, retrieval_rerank, warmup_check, capability_select
- [ ] **Traces** — turn type standard, turn type translate, runs warmup qualifies
- [ ] **Metriques** — tokens, latence, k retrieval/rerank, ctx_ratio, budget_used_pct, load time, TTFT, tok/s, passCount warmup

---

## 8. Items hors perimetre actuel (CDC explicitement reporte)

> Ces items sont documentes mais ne doivent PAS etre implementes maintenant.

- [N/A] **GraphRAG / LazyGraphRAG** — Reporte (CDC §11.9, §17.7)
- [N/A] **HyDE** — Fallback conditionnel seulement, pas dans le hot path (CDC §11.8, §17.5)
- [N/A] **M2 / M4 memoires** — Reportees a v4+ (CDC §17.2)
- [N/A] **Multi-runtime LLM parallele** — Rejete definitivement (CDC §17.4)
- [N/A] **SSO / AD** — Hors perimetre v3.0 (CDC §2.2)
- [N/A] **Redesign global endpoints** — Interdit (CDC API-001)

---

## Historique des interventions

| Date | Intervenant | Action |
|---|---|---|
| 2026-04-14 | Claude (Opus 4.6) | Creation du document. Verification complete Phase 0 (tout fait). Identification des ecarts retrieval, DTO, gouvernance, evaluation |
| 2026-04-14 | Claude (Opus 4.6) | Evidence pack DTO enrichi : snippet, categoryPath, rerankScore, hasTable, hasWarning, contextualSnippet, provenance offsets, metrics detaillees (teiMs, qdrantMs, candidatesEvaluated). TtlSeconds=600. Autocut dynamique. Diversite par section (max 2). 358 tests passent (0 echec). |
| 2026-04-14 | Claude (Opus 4.6) | maxPerDoc default aligne CDC (cap 3). Tests CDC v3.0 alignment ajoutes (RetrievalCdcV3AlignmentTests.cs): autocut 7 tests, snippet 4 tests, hasTable 3 tests, hasWarning 3 tests, section diversity 2 tests, maxPerDoc 1 test. Total: 378 tests (139 backend + 239 client), 0 echec. |
| | | |
