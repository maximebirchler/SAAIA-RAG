# Audit - LLM orchestration vs restrictions deterministes

Date: 2026-07-06, Europe/Zurich.
Repo: `C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG`.
Branche observee: `SAAIA_V3.1`.
HEAD observe: `d2797b80 Improve source-backed planning orchestration`.

## 1. Intention

Cet audit part de l'hypothese utilisateur suivante:

- le LLM client doit rester l'orchestrateur et le decideur final;
- le code doit fournir les outils, les contrats, les traces et les garde-fous;
- le code doit verifier que les informations finales sont reliees a des sources recuperees/lues;
- le code ne devrait pas decider semantiquement si une source "repond" a la question, sauf pour les cas mecaniques evidents: source absente, doublon exact, injection, JSON invalide, outil inutilisable, absence totale de preuve.

Conclusion courte: l'hypothese est largement confirmee. Le pipeline actuel contient des pieces LLM utiles, mais elles arrivent souvent apres une selection deterministe deja tres forte. Dans plusieurs chemins, le code ne se limite pas a verifier la reponse: il choisit les candidats, remplit les slots, reconstruit une reponse ou remplace le writer.

## 2. Chemin actuel observe

### 2.1 Router et premiere recherche

Fichiers:

- `client/SAAIA.Client.WinUI/ToolAgent/PromptCatalog.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs`

Le prompt router donne deja une bonne intention:

- broad documentary requests: utiliser categories/tree/navigation puis `rag.multi_search`;
- broad multi-slot plans: ne pas se limiter a la requete litterale;
- preserve explicit distinct slots.

Voir notamment:

- `PromptCatalog.cs:50-53`
- `PromptCatalog.cs:94-95`

Mais ensuite, le code detecte lui-meme beaucoup de formes de demande avec des regex multilingues:

- `LooksLikeSourceBackedPlanningRequest`: `ToolAgentOrchestrator.State.cs:4934`
- `LooksLikeWeeklyPlanningRequest`: `ToolAgentOrchestrator.State.cs:5020`
- `LooksLikeDocumentaryPlanningRequest`: `ToolAgentOrchestrator.State.cs:5032`
- `LooksLikeSourceBackedOptionRequest`: `ToolAgentOrchestrator.State.cs:5054`
- `RequiresStructuredSourceBackedPlanningCoverage`: `ToolAgentOrchestrator.State.cs:13342`
- `ShouldGateStructuredSourceBackedPlanningCoverage`: `ToolAgentOrchestrator.State.cs:13349`

Analyse:

- Detecter la forme de la demande est utile pour donner des outils au LLM.
- Le risque commence quand cette detection declenche un mode strict qui force ensuite une couverture, un nombre de candidats, des slots, un rebuild ou un fallback.

### 2.2 Exploration retrieval

Fichiers:

- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.cs`
- `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs`

Le chemin d'exploration est dans `TryExpandSourceBackedEvidenceRetrievalAsync`, a partir de `ToolAgentOrchestrator.cs:1842`.

Le LLM planner existe et est bien pense dans l'intention:

- `ShouldUseLlmSourceBackedEvidencePlanner`: `ToolAgentOrchestrator.cs:4134`
- `BuildSourceBackedLlmEvidenceExplorationSystemPrompt`: `ToolAgentOrchestrator.cs:4186`
- `BuildSourceBackedLlmEvidenceExplorationUserPrompt`: `ToolAgentOrchestrator.cs:4237`
- execution du planner: `ToolAgentOrchestrator.cs:3838`

Le prompt dit au LLM de choisir la prochaine voie de retrieval, d'utiliser `rag.search`, `rag.multi_search`, `documents.context`, de couvrir les slots, de pivoter si les queries generiques echouent.

Probleme: apres generation par le LLM, les passes sont filtrees/rewritees par le code:

- parsing des tool calls: `ToolAgentOrchestrator.State.cs:9127`
- limites globales: `MaxSourceBackedLlmEvidenceExplorationPasses = 4`, `Rounds = 2`, `Queries = 10` a `ToolAgentOrchestrator.State.cs:6286`
- filtre structured-axis: `FilterLowQualityStructuredAxisLlmEvidenceExplorationPasses` a `ToolAgentOrchestrator.State.cs:8487`
- rejection "low quality" de passes LLM: `ShouldRejectLowQualityStructuredAxisLlmEvidenceExplorationPass` a `ToolAgentOrchestrator.State.cs:8590`
- rewrite/completion de queries: `SanitizeStructuredAxisLlmEvidenceExplorationQueries` a `ToolAgentOrchestrator.State.cs:8717`

Analyse:

- Les sanitizers securite sont legitimes: bloquer injection, JSON dangereux, requetes trop longues, tool abuse.
- Les filtres "low quality", "decorative", "axis-only", "slot coverage" sont des decisions semantiques. Ils devraient probablement devenir des warnings dans le prompt ou des traces, pas des hard rejections.

### 2.3 Acceptation des passes de recherche

Apres un pass retrieval, le code decide s'il est accepte:

- analyse du candidat: `AnalyzeSourceBackedEvidenceSufficiency` a `ToolAgentOrchestrator.State.cs:11925`
- decision final pass: `ToolAgentOrchestrator.cs:2578-2647`
- rejet si regression de couverture: `ToolAgentOrchestrator.cs:2470-2481`
- rejet si pas de gain candidat: `ToolAgentOrchestrator.cs:2588-2627`

Analyse:

- Mesurer le score, le nombre de pages, le nombre de candidats et la diversite est utile.
- Rejeter une recherche parce qu'elle ne fait pas progresser une metrique heuristique peut masquer une source que le LLM aurait jugee pertinente.
- Le code devrait garder les resultats et annoter "faible gain", plutot que les exclure de la suite, sauf si le resultat est vide, hors scope explicite, ou dangereux.

### 2.4 Selection de candidats

Le coeur le plus restrictif est ici:

- `SelectSourceBackedPlanningCandidates`: `ToolAgentOrchestrator.State.cs:16199`
- extraction option candidates: `ToolAgentOrchestrator.State.cs:16299`
- premier filtre: `ExplainSourceBackedPlanningCandidateRejection` a `ToolAgentOrchestrator.State.cs:12773`
- fallback hits et fallback candidates: `ToolAgentOrchestrator.State.cs:16390-16599`

Le code rejette ou classe selon de nombreuses notions:

- `page_reference_only`
- `orientation_surface`
- `missing_candidate_title`
- `plan_item_noise`
- `weak_candidate_title`
- `generic_inventory_surface`
- `generic_planning_context`
- `requested_axis_label`
- `planning_frame_or_advice`
- `weak_single_term_candidate`
- `unattached_section_heading`
- `procedure_sentence_title`
- `field_value`
- `outside_dominant_scope`
- `not_concrete_candidate_title`
- `missing_strict_candidate_evidence`
- `not_relevant_to_query`

Voir `ToolAgentOrchestrator.State.cs:12780-12830`.

Analyse:

- Certaines raisons sont des garde-fous raisonnables si elles servent a annoter: page reference only, missing title, unsafe/no evidence.
- La plupart sont semantiques: weak, generic, planning frame, candidate title concrete, strict evidence. Elles peuvent etre utiles comme signaux, mais pas comme juge final.
- Pour le LLM, il serait plus sain de recevoir un inventaire large avec `codeHints.reasons`, et de decider lui-meme ce qui est utile.

### 2.5 Construction deterministe d'un plan

Le code construit lui-meme des reponses:

- `BuildSourceBackedPlanningAnswer`: `ToolAgentOrchestrator.State.cs:10484`
- `BuildSourceBackedPlanningDraft`: `ToolAgentOrchestrator.State.cs:10509`
- `BuildStructuredSourceBackedPlanAnswer`: `ToolAgentOrchestrator.State.cs:10990`
- `BuildStructuredSourceBackedSlotAwareGrid`: `ToolAgentOrchestrator.State.cs:11102`
- `BuildDistinctStructuredPlanningSlotAwareGrid`: `ToolAgentOrchestrator.State.cs:11187`
- `BuildRotatingStructuredPlanningSlotAwareGrid`: `ToolAgentOrchestrator.State.cs:11216`
- `BuildStructuredSourceBackedCandidateBankAnswer`: `ToolAgentOrchestrator.State.cs:11462`
- readable partial fallback: `ToolAgentOrchestrator.State.cs:32725`
- structured readable partial fallback: `ToolAgentOrchestrator.State.cs:32827`

Analyse:

- Ces fonctions depassent clairement le role de verification.
- Elles decident du contenu final, de la grille, des slots, de la rotation, du texte de limitation, du nombre minimal d'items et de la forme utilisateur.
- Elles expliquent tres bien pourquoi la reponse live peut devenir un fallback du type "les sources couvrent seulement une partie", meme quand le LLM aurait pu proposer une organisation plus utile.

### 2.6 Pre-writer branch

Dans `ToolAgentOrchestrator.cs:821-1049`, le code essaie de produire une reponse source-backed avant meme de passer au writer.

Exemples:

- category overview: `ToolAgentOrchestrator.cs:839-844`
- countdown planning: `ToolAgentOrchestrator.cs:845-856`
- documentary planning: `ToolAgentOrchestrator.cs:857-885`
- option answer: `ToolAgentOrchestrator.cs:886-897`
- extractive fallback: `ToolAgentOrchestrator.cs:898-905`
- return direct deterministic answer: `ToolAgentOrchestrator.cs:1030-1048`

Analyse:

- Pour une demande simple extractive, cela peut etre efficace.
- Pour une demande large ou creative sous contrainte sourcee, ce chemin devrait etre evite par defaut. Il court-circuite le LLM orchestrateur.

### 2.7 Candidate adjudication LLM

Il existe une bonne piece a conserver:

- `TryBuildSourceBackedCandidateAdjudicationForWriterAsync`: `ToolAgentOrchestrator.cs:11840`
- `ShouldRunSourceBackedCandidateAdjudicationForWriter`: `ToolAgentOrchestrator.cs:11916`
- `BuildSourceBackedCandidateAdjudicationSystemPrompt`: `ToolAgentOrchestrator.cs:11936`
- `BuildSourceBackedCandidateAdjudicationUserPrompt`: `ToolAgentOrchestrator.cs:11957`

Le prompt donne au LLM le role de juger les candidats:

- valid/useful/duplicate;
- decision use_candidates/partial/insufficient;
- missing slots.

Probleme: cette adjudication arrive apres la construction d'un inventaire deja filtre par `BuildSourceBackedCandidateLeadsForWriter`, qui s'appuie sur `SelectSourceBackedPlanningCandidates`.

Donc le LLM juge souvent un sous-ensemble prepare par le code, pas le materiau brut recupere.

### 2.8 Finalizers post-writer

Le code finalise et peut remplacer la reponse du writer a plusieurs endroits:

- pre-writer finalizer: `ToolAgentOrchestrator.cs:981-1014`
- outer finalizer: `ToolAgentOrchestrator.cs:1169-1203`
- structured guard rebuild: `ToolAgentOrchestrator.cs:1236-1289`
- outer last-mile finalizer: `ToolAgentOrchestrator.cs:1291-1325`
- AnswerAsync finalizer: `ToolAgentOrchestrator.cs:11680-11706`
- core finalizer: `TryFinalizeSourceBackedPlanningResponse` a `ToolAgentOrchestrator.State.cs:14777`

Dans le strict planning path, si le writer ne passe pas:

- le code tente `TryBuildSupportedStructuredPlanningAnswer`;
- sinon tente un partial rebuild;
- sinon fabrique une insuffisance deterministe.

Voir `ToolAgentOrchestrator.State.cs:14802-14873`.

Analyse:

- Verifier que les items finaux sont supportes est legitime.
- Remplacer par un plan deterministe ou une insuffisance deterministe est beaucoup plus discutable.
- Le bon role serait: verifier, retourner au LLM avec erreurs de contrat, puis seulement fallback minimal si le LLM echoue.

## 3. Garde-fous a conserver

Ces elements devraient rester cote code:

1. Securite et hygiene tool/JSON:
   - JSON strict;
   - tool calls autorises uniquement;
   - rejet des requetes d'injection ou system prompt leak;
   - limites de taille, timeout, cancellation.

2. Verification source finale:
   - chaque item concret final doit pouvoir etre relie a au moins une source;
   - les sources visibles doivent etre issues des hits/tool results;
   - les doublons source/page doivent etre fusionnes ou signales;
   - aucune source inventee.

3. Normalisation technique:
   - extraction de docId/docPath/page;
   - merge de sources identiques;
   - suppression des blocs "Sources:" emis par le modele si l'app fournit deja les cartes;
   - nettoyage OCR leger quand il ne change pas le fait source.

4. Observabilite:
   - traces des queries;
   - traces des candidats;
   - traces des raisons de rejet, mais en tant que diagnostics;
   - traces des decisions LLM.

## 4. Restrictions a detendre ou deplacer vers le LLM

Priorite haute:

1. Ne plus laisser le pre-writer produire un plan structure large par defaut.
   - Conserver pour les demandes simples exactes/extractives.
   - Pour broad planning, route vers writer/LLM avec inventaire source.

2. Transformer la selection de candidats en inventaire annote.
   - Ne pas supprimer les candidats "weak/generic/noisy" trop tot.
   - Les exposer au LLM avec `codeHints` et `riskFlags`.
   - Garder un filtre dur seulement pour: vide, unsafe, hors tool result, source absente.

3. Reduire les finalizers rebuild.
   - Le finalizer doit verifier le contrat et lister les erreurs.
   - Si erreur: re-prompt writer avec "ces items ne sont pas supportes / ces sources manquent".
   - Ne reconstruire deterministiquement que si le LLM timeout/echec deux fois, et marquer explicitement la resolution.

4. Rendre le LLM planner plus libre.
   - Garder sanitization securite.
   - Convertir `FilterLowQualityStructuredAxisLlmEvidenceExplorationPasses` en warning/trace.
   - Ne pas rejeter une passe safe parce qu'elle semble "decorative" ou "axis-only"; laisser au moins une execution si budget disponible.

5. Changer la notion de "coverage".
   - Les metriques `CandidateCount`, `MinimumCandidateCount`, `TargetSlotCount`, `DistinctSourcePages` doivent guider le LLM.
   - Elles ne devraient pas bloquer seules une reponse partielle utile.

## 5. Architecture cible proposee

Chemin cible:

```text
question utilisateur
-> LLM router interprete la demande et choisit une premiere recherche
-> outils RAG/documents retournent hits + pages + sommaires + navigation + contexte
-> LLM planner observe la couverture et decide: reformuler, elargir, lire sommaire, lire page, changer scope, chercher synonymes
-> code execute les tool calls safe et garde toutes les observations sourcees
-> LLM adjudicator/writer choisit les sources utiles et construit la reponse
-> code verifie le contrat final: items concrets source-backed, sources visibles, pas de source inventee, doublons geres
-> si contrat KO: correction LLM avec erreurs explicites
-> si correction impossible: fallback court et transparent
```

Ce qui change:

- le code ne decide plus "cette source repond a la question";
- le code dit "voici ce que j'ai trouve, voici les risques mecaniques, voici ce qui est cite ou non";
- le LLM decide quels elements sont utiles pour la demande.

## 6. Premier plan de refactor

### Phase A - Audit instrumentation sans changement produit

But: rendre visibles les endroits ou le code prend la main.

Actions:

- ajouter une trace `orchestration.authority` quand un chemin est:
  - `llm_decision`;
  - `deterministic_guard`;
  - `deterministic_semantic_filter`;
  - `deterministic_answer_rebuild`;
  - `source_contract_verification`.
- tracer combien de candidats bruts sont retires avant le LLM adjudicator.
- tracer quand un finalizer remplace une reponse writer.

### Phase B - Inventaire large pour le LLM

But: donner au LLM plus de matiere.

Actions:

- creer un builder d'inventaire "broad evidence inventory" depuis les hits bruts;
- inclure candidats acceptes, rejetes, navigation/context, low-signal, avec raisons;
- exposer cet inventaire au candidate adjudicator;
- ne pas limiter l'adjudicator aux seuls candidats de `SelectSourceBackedPlanningCandidates`.

### Phase C - Planner LLM moins bride

But: laisser le LLM explorer.

Actions:

- conserver `LooksLikeUnsafeGeneratedSourceBackedExplorationQuery`;
- garder longueur/dedup/tool limits;
- detendre `LooksLikeNoisyGeneratedSourceBackedExplorationQuery`;
- remplacer `FilterLowQualityStructuredAxisLlmEvidenceExplorationPasses` par:
  - sanitization hard pour unsafe;
  - warning pour low quality;
  - optional deterministic completion en pass supplementaire, pas remplacement.

### Phase D - Writer d'abord pour broad planning

But: eviter que le code construise la reponse finale.

Actions:

- pour `ShouldGateStructuredSourceBackedPlanningCoverage`, eviter le pre-writer deterministic answer si le writer est disponible;
- faire passer le writer avec:
  - raw hits;
  - evidence inventory;
  - candidate adjudication LLM;
  - source contract.

### Phase E - Finalizer = verifier, pas auteur

But: garder la securite source sans reprendre la decision.

Actions:

- `TryFinalizeSourceBackedPlanningResponse` devrait d'abord retourner un resultat de verification:
  - ok;
  - unsupportedItems;
  - missingSources;
  - duplicateSources;
  - tooFewCitations;
  - suggestedRepairPrompt.
- si KO: appeler le writer repair.
- fallback deterministe seulement apres echec/timeout, avec trace explicite.

## 7. Tests a adapter ou ajouter

Tests a revoir car ils figent probablement l'ancien comportement deterministe:

- `StructuredPlanningCoverageTests.cs`
- `SourceBackedEvidencePlannerObservabilityTests.cs`
- `LiveCuisineAgentValidationTests.cs`

Nouveaux tests recommandes:

1. `Llm_led_planning_passes_raw_and_rejected_candidates_to_adjudicator`
   - Verifie que les candidats rejetes par heuristique apparaissent quand meme dans l'inventaire LLM avec un reason flag.

2. `Llm_planner_safe_axis_query_is_warned_not_rejected`
   - Une query "faible" mais safe doit etre executee ou conservee si budget disponible.

3. `Structured_planning_writer_response_is_repaired_before_deterministic_rebuild`
   - Si le writer a des items non sources, le code doit appeler repair avant rebuild.

4. `Source_contract_verifier_rejects_unsourced_final_item_without_judging_semantic_fit`
   - Le code rejette un item invente parce qu'il n'est pas source, pas parce qu'il "ne fit pas" la question.

5. `Candidate_adjudicator_receives_navigation_context_and_low_signal_hits_as_private_evidence`
   - Le LLM voit les pages/navigation/context pour juger lui-meme.

## 8. Risques

- Detendre trop vite les filtres peut augmenter les reponses bruittees si le writer n'est pas bien contraint.
- Les tests actuels sont tres lies aux heuristiques; certains vont devoir changer de philosophie.
- Le live peut devenir plus long, car le LLM explorera plus librement.
- Il faudra surveiller le budget prompt: donner plus de materiau brut au LLM exige un inventaire compact et bien structure.

## 9. Prochaine action concrete

Je recommande de commencer par une petite modification non destructive:

1. ajouter un inventaire large annote pour le writer/adjudicator;
2. garder l'ancien candidat strict en parallele;
3. tracer les differences;
4. ne pas encore supprimer les filtres;
5. verifier sur le scenario live si le LLM voit enfin les bons elements au lieu d'etre bloque sur un fallback.

Ensuite seulement, detendre:

- les rejections du planner LLM;
- le pre-writer deterministic planning;
- les rebuilds finalizer.
