# SAAIA â€” Ecarts CDC v3.0 vs Code â€” Document de suivi vivant

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

## 0. Phase 0 â€” Alignement contractuel et semantique (CDC Â§18)

> **Statut global : FAIT**
> Verifie le 2026-04-14. Tous les points P0 de l'audit v2.9.1 ont ete corriges.

- [x] **Manifest version bumped to v3.0** â€” `ToolManifest.cs` utilise `"v3.0"`
- [x] **meta.repair_last -> meta.rewrite_last** â€” `PromptCatalog.cs` et `ToolAgentOrchestrator.cs` utilisent `meta.rewrite_last` ; "repair" accepte comme alias legacy normalise
- [x] **admin.summary.store -> admin.summary.submit** â€” Tool `admin.summary.submit` dans manifest, endpoint `POST /admin/summaries/submit` dans backend
- [x] **strictMode supprime** â€” `AppSettings.cs` migre vers `ActiveMode` (auto/standard/strict) ; lecture legacy avec nettoyage actif (`ls.Values.Remove("llm.strictMode")`)
- [x] **responseStyle -> responseFormat** â€” `RouterPlan.cs` et `PromptCatalog.cs` utilisent `responseFormat`
- [x] **BuildConversationManifestJson() implemente** â€” Filtre `access=user` uniquement pour le rail conversation libre, sans condition sur session admin
- [x] **POST /sources/resolve** â€” Endpoint implemente dans `SourcesEndpoints.cs`
- [x] **POST /documents/resolve-category** â€” Endpoint implemente dans `DocumentsEndpoints.ResolveCategory.cs`
- [x] **ETag/304 sur GET /documents/tree** â€” SHA256 ETag + If-None-Match + 304 dans `DocumentsEndpoints.cs`
- [x] **user-prefs.bin (UserPrefsStore)** â€” Implemente avec DPAPI dans `UserPrefsStore.cs`, persistance langue/style/mode
- [x] **display_order en SQL** â€” Table `documents_catalog_categories` avec `display_order int NOT NULL` + index

---

## 1. Pipeline Retrieval â€” Ecarts structurels (CDC Â§11)

> **Statut global : LARGEMENT FAIT**
> Le pipeline exact-match -> sparse -> dense -> rerank -> linked est en place. Les briques CDC restantes sont maintenant surtout la mesure, le calibrage et les raffinements de convergence.

### 1.1 BM25 / Sparse Retrieval (CDC Â§11.2)
- [x] **Retrieval sparse/BM25 implemente** Ã¢â‚¬â€ FAIT 2026-04-16 (Codex)
  - CDC : "BM25" est explicitement liste dans le pipeline candidate selection
  - Implemente : `SearchSparseMatchesAsync()` dans `RagEndpoints.cs` avec PostgreSQL FTS (`websearch_to_tsquery('simple', ...)`) sur `contextual_text_entries`
  - Schema : migration `024_retrieval_sparse_indexes.sql` avec index GIN `to_tsvector('simple', text_content)`
  - Observabilite : provenance `sparse_bm25`, metrics `SparseReturned` + `SparseMs`
  - Etat : le retrieval exact-match / sparse / dense tourne maintenant ensemble en runtime

### 1.2 Fusion RRF â€” Reciprocal Rank Fusion (CDC Â§11.2-11.3)
- [x] **Fusion RRF implemente pour exact + dense + sparse** Ã¢â‚¬â€ FAIT 2026-04-16 (Codex)
  - CDC : "fusion RRF" explicitement demandee
  - Implemente : `FuseWithRrf()` dans `RagEndpoints.cs`
  - Algorithme : `RRF_score(d) = sum(1 / (k + rank_i(d)))` pour chaque retriever i
  - Logique : dedup par extrait/document puis selection d'un representant par priorite (`exact_match > sparse_bm25 > dense_qdrant`)
  - Etat : `linked_context` reste volontairement une expansion aval, hors fusion RRF

### 1.3 TEI /rerank (CDC Â§11.3)
- [x] **Reranking TEI integre de facon progressive** - FAIT 2026-04-16 (Codex)
  - CDC : "TEI /rerank" dans l'etape evidence selection
  - Implemente : `TeiClient.RerankAsync()` sur endpoint `/rerank`, branche dans `TryRerankWithTeiAsync()` apres la fusion RRF
  - Activation : optionnelle via `Rag.EnableRerank`; fallback propre si desactivee ou si TEI rerank indisponible
  - Contrat : `RagItemDto.RerankScore` peuple pour les candidats rerankes, `metrics.RerankMs` expose le cout du rerank
  - Fichiers concernes : `TeiClient.cs` (ajout `RerankAsync()`), `RagEndpoints.cs` (appel post-fusion), `RagSearchDto.cs` (ajout `rerankScore`)

### 1.4 Autocut dynamique (CDC Â§11.3)
- [x] **Implementer un autocut absolu + relatif** â€” FAIT 2026-04-14 (Claude)
  - CDC : "autocut absolu + relatif" dans evidence selection
  - Implemente : `ApplyAutocut()` dans `RagEndpoints.cs` â€” detecte la plus grande chute relative (>= 15%) entre scores consecutifs, coupe apres le gap. Enforce aussi le seuil absolu `minScore` sur les non-exact-match
  - Tests : 358 tests passent (119 backend + 239 client)

### 1.5 Diversite par section (CDC Â§11.3)
- [x] **Max 2 chunks / section implemente** â€” FAIT 2026-04-14 (Claude)
  - CDC : "diversite max 3 chunks / document, max 2 chunks / section"
  - Implemente : `maxPerSection = 2` dans `AddRankedMatches()` avec `BuildSectionKey()` (SectionOrdinal ou SectionTitle fallback)
  - Tests : 358 tests passent

### 1.6 Diversite par document (CDC Â§11.3)
- [x] **maxPerDoc default aligne sur CDC (max 3)** â€” FAIT 2026-04-14 (Claude)
  - CDC : "diversite max 3 chunks / document"
  - Implemente : `Math.Min(3, Math.Max(2, topK / 2))` en balanced, `Math.Min(topK, 3)` en focused, `2` en broad
  - Overridable via `req.MaxPerDoc` ou `req.Diversity.MaxChunksPerDoc`
  - Tests : 378 tests passent (139 backend + 239 client)

---

## 2. Evidence Pack â€” Contrat DTO (CDC Â§11.6)

> **Statut global : LARGEMENT FAIT** (mise a jour 2026-04-14)
> Les champs de base + enrichis CDC v3.0 sont presents. Restent : categoryRef, provenance offsets reels, hypQuestionsMatched.

### 2.1 Champs presents et conformes
- [x] `requestId` â€” present dans `RagSearchResponseDto`
- [x] `query` / `queryNormalized` â€” present
- [x] `retrieversUsed` â€” present dans `RagMetricsDto`
- [x] `topK` â€” present
- [x] `dataHash` â€” present (SHA256 calcule)
- [x] `score` â€” present dans `RagItemDto`
- [x] `docId`, `docName`, `docPath` â€” presents
- [x] `pageStart`, `pageEnd` â€” presents
- [x] `chunkId`, `chunkIndex` â€” presents
- [x] `text` â€” present (contenu du chunk)
- [x] `sourceHash` â€” present
- [x] `exactMatchHit` â€” present
- [x] `chunkType` â€” present
- [x] `headingPath` â€” present
- [x] `metrics.tookMs`, `metrics.returned` â€” presents

### 2.2 Champs manquants ou non conformes
- [x] **`ttlSeconds`** â€” FAIT 2026-04-14 (Claude). Default = 600 dans `RagMetricsDto` et dans la construction de reponse
- [x] **`categoryPath`** â€” FAIT 2026-04-14 (Claude). Ajoute en champ additionnel dans `RagItemDto` (peuple avec la meme valeur que `category` pour backward compat). `category` conserve pour ne pas casser le client
- [ ] **`categoryRef`** â€” Reference ordinale optionnelle. Absente. Necessite resolution backend via `documents_catalog_categories.display_order`
- [x] **`snippet`** â€” FAIT 2026-04-14 (Claude). `BuildSnippet()` dans `RagEndpoints.cs` â€” tronque a 500 chars sur frontiere de phrase ou mot
- [x] **`rerankScore`** â€” FAIT 2026-04-14 (Claude). Champ present dans le DTO (`RagItemDto.RerankScore`), peuple a `null` en attendant l'integration TEI /rerank (Â§1.3)
- [x] **`provenance.offsetStart` / `provenance.offsetEnd`** â€” FAIT 2026-04-14 (Claude). Champs ajoutes dans `RagItemProvenanceDto`. Peuples a `null` â€” le calcul d'offset depend du parseur PDF et n'est pas encore implemente
- [x] **`hasTable`** â€” FAIT 2026-04-14 (Claude). Detection heuristique par comptage de lignes avec pipes (`DetectHasTable()`)
- [x] **`hasWarning`** â€” FAIT 2026-04-14 (Claude). Detection par chunkType "warning" ou mots-cles (WARNING, DANGER, CAUTION, AVERTISSEMENT, ATTENTION)
- [x] **`contextualSnippet`** â€” FAIT 2026-04-14 (Claude). Peuple avec `embedText` (le texte contextuel utilise pour l'embedding)
- [ ] **`hypQuestionsMatched`** â€” Indicateur HyPE. Absent (depend Capacite A, non prioritaire)
- [x] **`metrics.teiMs`** â€” FAIT 2026-04-14 (Claude). Ajoute dans `RagMetricsDto.TeiMs`, peuple depuis `resp.Timings.TeiMs`
- [x] **`metrics.qdrantMs`** â€” FAIT 2026-04-14 (Claude). Ajoute dans `RagMetricsDto.QdrantMs`, peuple depuis `resp.Timings.QdrantMs`
- [x] **`metrics.candidates`** â€” FAIT 2026-04-14 (Claude). Ajoute comme `RagMetricsDto.CandidatesEvaluated`, peuple depuis `resp.Candidates`

---

## 3. Gouvernance des modeles (CDC Â§9)

> **Statut global : NON COMMENCE**
> Le CDC v3.0 introduit un systeme complet de gouvernance. Rien n'existe encore dans le code.

- [ ] **model_catalog.json** â€” Catalogue approuve de modeles client et serveur (CDC Â§5.8). Non cree
- [ ] **runtime_catalog.json** â€” Catalogue des runtimes approuves et hard gates (CDC Â§5.8). Non cree
- [ ] **warmup_profiles.json** â€” Profils de qualification versionnes (CDC Â§5.8). Non cree
- [ ] **warmup_results.json** â€” Resultats de qualification locale (CDC Â§5.8). Non cree
- [ ] **capability_state.json** â€” Etat des capacites installees/configurees/healthy/qualifiees/autorisees/selectionnees (CDC Â§5.8). Non cree
- [ ] **Warmup gate logic** â€” Validation {runtime, modele, quantification, profil materiel} avant activation (CDC Â§9.6). Non implemente
- [ ] **Hard gates** â€” Filtrage modeles par prerequis materiel (CDC Â§9.3). Non implemente
- [ ] **Chaine de decision** â€” Installee -> Configuree -> Healthy -> Qualifiee -> Autorisee -> Selectionnee (CDC Â§9.7). Non implementee
- [ ] **Migration LocalLlmBootstrapper.cs** â€” Actuellement hardcode sur 6 modeles (Qwen Q4_0/Q4_K_S/Q4_K_M/Q6_K + Mistral Q4_K_M/Q6_K). Doit migrer vers catalogue approuve filtre par profil materiel
- [ ] **Profils client C1/C2/C3/C4** â€” Selection par VRAM detectee dans un catalogue, pas dans du code hardcode (CDC Â§9.3)
- [ ] **Diagnostics de qualification** â€” Exposer l'etat via `GET /admin/runtime/capabilities`, `GET /admin/runtime/catalog`, `POST /admin/runtime/requalify` (CDC Â§7.3)

---

## 4. Capacites serveur optionnelles A/B/C (CDC Â§4.4)

> **Statut global : NON COMMENCE**
> Infrastructure a creer. Le CDC est clair : aucune dependance fonctionnelle sur le coeur.

- [ ] **Abstraction Capability** â€” Creer un systeme de capabilities avec etats (installee/configuree/healthy/qualifiee/autorisee/selectionnee)
- [ ] **Capacite A (CorpusEnrichment)** â€” Contextualisation LLM des chunks, HyPE, enrichissement metadata, scoring qualite, auto-tagging. Actuellement le contextual_text est code-based (conforme si A absente)
- [ ] **Capacite B (BackofficeGeneration)** â€” Resumes stockes, syntheses multi-docs, exports enrichis, jobs backoffice. Le worker resume admin existe partiellement
- [ ] **Capacite C (RetrievalIntelligence)** â€” Reformulation, decomposition, HyDE fallback, compression contextuelle. Experimentale, desactivee par defaut
- [ ] **Politique d'activation** â€” "Installee != activee != selectionnee" (CDC Â§3.3 MOD-003/MOD-004)

---

## 5. Metriques d'evaluation contractuelles (CDC Â§15.3)

> **Statut global : PARTIELLEMENT FAIT**
> Le harnais de regression existe (corpus v1/v2/v3 + tests). Les metriques CDC ne sont pas mesurees.

### 5.1 Harnais existant (fonctionnel)
- [x] `retrieval_eval_corpus.v1.json` â€” Baseline exact lookup
- [x] `retrieval_eval_corpus.v2.json` â€” Dominant retriever + linked context
- [x] `retrieval_eval_corpus.v3.json` â€” Business-oriented expectations (5+ cas)
- [x] `RetrievalEvaluationHarnessTests.cs` â€” Tests recall, noise, channel ordering, business decisions
- [x] `RetrievalEvaluationCorpusTests.cs` â€” Validation corpus structure
- [x] `retrieval-regression-runbook.v3.md` â€” Runbook de campagne
- [x] `run-retrieval-regression.ps1` â€” Script PowerShell de campagne

### 5.2 Metriques CDC manquantes
- [ ] **Context Precision > 0.75** â€” Non mesuree. Necessite ground truth annote
- [ ] **Context Recall > 0.80** â€” Non mesuree. Necessite ground truth annote
- [ ] **Faithfulness > 0.85** â€” Non mesuree. Necessite evaluation end-to-end avec LLM
- [ ] **Answer Relevancy > 0.80** â€” Non mesuree. Necessite evaluation end-to-end avec LLM
- [ ] **Exact Match Hit Rate > 0.95 sur familles ciblees** â€” Partiellement (recall testee sur corpus synthetique, pas sur vrais documents)
- [ ] **Retrieval P95 < 800ms** â€” Non mesuree. Ajouter mesure de latence dans le harnais
- [ ] **Rerank P95 < 300ms** â€” N/A tant que TEI /rerank n'est pas implemente (Â§1.3)
- [ ] **Zero-result rate < 5%** â€” Non mesuree. Ajouter compteur de requetes sans resultats

### 5.3 Familles de tests obligatoires (CDC Â§15.4)
- [~] **Famille 1 : References exactes** â€” Couverte partiellement par corpus v3 exactPositiveCases
- [ ] **Famille 2 : Procedures et modes operatoires** â€” Non couverte avec documents reels
- [ ] **Famille 3 : Syntheses mono-document** â€” Non couverte
- [ ] **Famille 4 : Comparaisons multi-documents** â€” Non couverte

---

## 6. Structure documentaire (CDC Â§5.4)

> **Statut global : LARGEMENT FAIT**

- [x] `documents` â€” Table presente avec identite canonique, versions, compteurs, etat
- [x] `document_sections` â€” Hierarchie logique (heading, breadcrumb, pages)
- [x] `document_units` â€” Plus petite unite typee extraite
- [x] `retrieval_chunks` â€” Objets derives optimises pour la recherche
- [x] `retrieval_chunk_links` â€” Navigation prev/next/same_section
- [x] `exact_match_entries` â€” Index deterministe normes/codes/modeles
- [~] `document_pages` / `document_page_index` â€” `document_page_index` existe (revision_id, page_number, char_count, checksum, metadata). CDC mentionne `document_pages` avec ancrage page, disponibilite texte natif, usage OCR, score de parsing â€” champs supplementaires potentiellement manquants

---

## 7. Observabilite OTel (CDC Â§15.2)

> **Statut global : NON VERIFIE**
> A auditer. docker-compose.otel.yml existe.

- [ ] **Spans CDC requis** â€” pre_router, router_llm, context_builder, tool[N], writer_llm, critic_llm, retrieval_exact_match, retrieval_dense, retrieval_sparse, retrieval_rerank, warmup_check, capability_select
- [ ] **Traces** â€” turn type standard, turn type translate, runs warmup qualifies
- [ ] **Metriques** â€” tokens, latence, k retrieval/rerank, ctx_ratio, budget_used_pct, load time, TTFT, tok/s, passCount warmup

---

## 8. Items hors perimetre actuel (CDC explicitement reporte)

> Ces items sont documentes mais ne doivent PAS etre implementes maintenant.

- [N/A] **GraphRAG / LazyGraphRAG** â€” Reporte (CDC Â§11.9, Â§17.7)
- [N/A] **HyDE** â€” Fallback conditionnel seulement, pas dans le hot path (CDC Â§11.8, Â§17.5)
- [N/A] **M2 / M4 memoires** â€” Reportees a v4+ (CDC Â§17.2)
- [N/A] **Multi-runtime LLM parallele** â€” Rejete definitivement (CDC Â§17.4)
- [N/A] **SSO / AD** â€” Hors perimetre v3.0 (CDC Â§2.2)
- [N/A] **Redesign global endpoints** â€” Interdit (CDC API-001)

---

## Historique des interventions

| Date | Intervenant | Action |
|---|---|---|
| 2026-04-14 | Claude (Opus 4.6) | Creation du document. Verification complete Phase 0 (tout fait). Identification des ecarts retrieval, DTO, gouvernance, evaluation |
| 2026-04-14 | Claude (Opus 4.6) | Evidence pack DTO enrichi : snippet, categoryPath, rerankScore, hasTable, hasWarning, contextualSnippet, provenance offsets, metrics detaillees (teiMs, qdrantMs, candidatesEvaluated). TtlSeconds=600. Autocut dynamique. Diversite par section (max 2). 358 tests passent (0 echec). |
| 2026-04-14 | Claude (Opus 4.6) | maxPerDoc default aligne CDC (cap 3). Tests CDC v3.0 alignment ajoutes (RetrievalCdcV3AlignmentTests.cs): autocut 7 tests, snippet 4 tests, hasTable 3 tests, hasWarning 3 tests, section diversity 2 tests, maxPerDoc 1 test. Total: 378 tests (139 backend + 239 client), 0 echec. |
| | | |
