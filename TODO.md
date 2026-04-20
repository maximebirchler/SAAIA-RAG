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
- [x] **`categoryPath`** â€” FAIT 2026-04-14 (Claude), corrige 2026-04-19. Champ additionnel dans `RagItemDto`, maintenant peuple depuis le document matche plutot que depuis le filtre de requete
- [x] **`categoryRef`** â€” FAIT 2026-04-19. Reference ordinale optionnelle ajoutee a `/rag/search`, resolue via `documents_catalog_categories.display_order`
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

> **Statut global : PARTIELLEMENT FAIT**
> Un premier socle backend existe maintenant : persistance d'etat runtime, resultats de warmup, endpoints admin runtime et qualification minimale du coeur retrieval. Le lifecycle CDC complet reste a finir.

- [~] **model_catalog.json** â€” Artefact admin expose via `GET /admin/runtime/artifacts/model-catalog.json` depuis 2026-04-19 ; contenu encore derive du runtime retrieval plutot qu'un catalogue modele multi-runtime complet
- [~] **runtime_catalog.json** â€” Artefact admin expose via `GET /admin/runtime/artifacts/runtime-catalog.json` depuis 2026-04-19 ; pas encore de versionnement/export disque dedie
- [~] **warmup_profiles.json** â€” Artefact admin expose via `GET /admin/runtime/artifacts/warmup-profiles.json` depuis 2026-04-19 avec hard gates minimaux + budgets de temps + policy de checks + exigences runtime attendues (`default-local`, `strict-local`, `strict-rerank`) ; profils CDC complets encore absents
- [~] **warmup_results.json** â€” Resultats persistants en base (`runtime_warmup_results`) + artefact admin `GET /admin/runtime/artifacts/warmup-results.json` depuis 2026-04-19 ; pas encore d'export versionne autonome
- [~] **capability_state.json** â€” Etat persistant en base (`runtime_capability_state`) + artefact admin `GET /admin/runtime/artifacts/capability-state.json` depuis 2026-04-19 ; politique multi-capacites encore simplifiee
- [~] **Warmup gate logic** â€” Qualification minimale implemente pour `core.retrieval` (Qdrant + TEI embeddings + rerank si active) avec `warmupPassCount=3` par defaut ; hard gates CPU/memoire/process 64-bit + budgets de temps + policy de checks + exigences runtime par profile ajoutes le 2026-04-19, details de checks/durees/mesures exposes dans les resultats, mais la qualification contractuelle reste encore simplifiee
- [~] **Hard gates** â€” Filtrage materiel minimal implemente (`MinCpuCores`, `MinAvailableMemoryMb`, `Require64BitProcess`) et maintenant applique par profil (`default-local`, `strict-local`, `strict-rerank`) ; budgets de temps de checks/warmup ajoutes (`MaxWarmupPassDurationMs`, `MaxQdrantCheckMs`, `MaxEmbeddingsCheckMs`, `MaxRerankCheckMs`), mais hard gates modeles/profils materiels CDC complets encore absents
- [~] **Chaine de decision** â€” Etats `installed/configured/healthy/qualified/authorized/selected` exposes et persistants pour `core.retrieval` ; requalification + selection admin implementees, mais politiques multi-capacites et hard gates complets restent incomplets
- [ ] **Migration LocalLlmBootstrapper.cs** â€” Actuellement hardcode sur 6 modeles (Qwen Q4_0/Q4_K_S/Q4_K_M/Q6_K + Mistral Q4_K_M/Q6_K). Doit migrer vers catalogue approuve filtre par profil materiel
- [ ] **Profils client C1/C2/C3/C4** â€” Selection par VRAM detectee dans un catalogue, pas dans du code hardcode (CDC Â§9.3)
- [x] **Diagnostics de qualification** â€” `GET /admin/runtime/capabilities`, `GET /admin/runtime/catalog`, `POST /admin/runtime/requalify`, `GET /admin/runtime/warmup-results`, `POST /admin/runtime/capabilities/{key}/selection` implementes le 2026-04-19
- [x] **Vue de synthese runtime** â€” `GET /admin/runtime/diagnostics` implemente le 2026-04-19 avec statuts, blockers, recommandations et resume agrege ; artefact exportable `GET /admin/runtime/artifacts/diagnostics.json` ajoute

---

## 4. Capacites serveur optionnelles A/B/C (CDC Â§4.4)

> **Statut global : AMORCE**
> Le backend expose maintenant un catalogue et un etat persistant pour A/B/C, mais les capacités restent explicitement non implementees.

- [~] **Abstraction Capability** â€” Catalogue + etat persistant + API admin runtime en place depuis 2026-04-19 ; selection/politiques encore minimales
- [~] **Capacite A (CorpusEnrichment)** â€” Gouvernance runtime/admin implementees, candidats et campagnes exposes, et apercus semantiques deterministes (preview texte, sections, tags, questions) ajoutes sur les candidats ; enrichissement HyPE complet / auto-tagging metier encore absents
- [~] **Capacite B (BackofficeGeneration)** â€” Gouvernance runtime/admin implementees, candidats/campagnes/detail de campagne exposes, contrat d'execution `claim/complete/fail` en place et worker backend deterministe de resumés backoffice ajoute ; runtime generatif serveur complet encore absent
- [ ] **Capacite C (RetrievalIntelligence)** â€” Toujours non implementee. Le runtime la presente explicitement comme absente
- [~] **Politique d'activation** â€” `desired_enabled`, `authorized`, `selected` persistants pour `core.retrieval` ; diagnostic admin de blocage/recommandation ajoute, mais politique A/B/C et separation fine `installed != activee != selectionnee` encore incomplètes

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
- [~] **Retrieval P95 < 800ms** â€” Histograms OTel retrieval ajoutes le 2026-04-19 (`saaia.retrieval.duration`, `*.exact_match.duration`, `*.sparse.duration`, `*.dense.duration`, `*.linked.duration`) ; seuil CDC pas encore mesure/publie
- [~] **Rerank P95 < 300ms** â€” Histogram OTel `saaia.retrieval.rerank.duration` ajoute ; seuil CDC pas encore mesure/publie
- [~] **Zero-result rate < 5%** â€” Compteur OTel `saaia.retrieval.zero_results` ajoute le 2026-04-19 ; taux agrege / dashboard encore manquant

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

> **Statut global : PARTIELLEMENT FAIT**
> Le wiring OTel generique existe, et le retrieval expose maintenant des spans/metriques metier. Restent les spans hors retrieval et les agrégats de gouvernance runtime.

- [~] **Spans CDC requis** â€” `rag.search`, `retrieval_exact_match`, `retrieval_dense`, `retrieval_sparse`, `retrieval_linked_context`, `retrieval_rerank`, `warmup_check`, `capability_select` ajoutes le 2026-04-19 ; restent `pre_router`, `router_llm`, `context_builder`, `tool[N]`, `writer_llm`, `critic_llm`
- [ ] **Traces** â€” turn type standard, turn type translate, runs warmup qualifies
- [~] **Metriques** â€” retrieval requests, zero-results, returned-results, candidates, latences `retrieval/exact/sparse/dense/linked/rerank/tei/qdrant`, plus `runtime.requalify.requests`, `runtime.selection.updates`, `runtime.warmup.passes`, `runtime.warmup.failures`, `runtime.warmup.budget_failures`, `runtime.warmup.duration`, `runtime.selection.duration`, `runtime.artifact.reads`, `runtime.artifact.duration` ajoutes le 2026-04-19 ; restent tokens, ctx_ratio, budget_used_pct, load time, TTFT, tok/s, dashboarding/metriques agregees CDC publiees

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
