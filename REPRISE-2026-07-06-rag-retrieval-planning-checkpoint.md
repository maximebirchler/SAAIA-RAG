# Reprise developpement - SAAIA RAG - retrieval, sources, planification

Snapshot: checkpoint de reprise apres corrections retrieval/candidats autour du plan de repas et des ancres documentaires.

## 1. Objectif de ce checkpoint

Le chantier en cours vise a fiabiliser le RAG pour les reponses sourcees, en particulier la question finale de validation:

> J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi en y mettant petit-dejeuner, diner, souper et gouter / collation chaque jour, avec un format clair et user-friendly, uniquement avec des sources utiles, sans doublons inutiles et sans invention.

Ce checkpoint ne cloture pas tout le chantier ingestion/OCR/indexation. Il documente un palier net cote client/retrieval/planning: plusieurs regressions autour de la selection de candidats, des titres de chunks, des sommaires et des slots ont ete corrigees et validees par tests.

## 2. Philosophie produit a conserver

- Le LLM principal doit rester l'orchestrateur cote client.
- Le serveur ne doit pas etre utilise comme LLM de reponse utilisateur. Le README actuel dit explicitement: serveur sans generation LLM, LLM obligatoire cote client.
- Le backend sert a l'ingestion, l'indexation, le retrieval source, le chat-store, la securite et l'observabilite.
- Les corrections doivent rester generiques. Rien ne doit etre code en dur pour la cuisine.
- Les exemples cuisine existent dans les tests parce que le scenario final est un plan de repas, mais la logique principale doit parler en termes generiques: candidat, titre ancre, preuve locale, sommaire, page, document, slot, route de retrieval.
- Quand il y a des sources partielles valides, eviter une sortie pauvre du type "pas assez pour repondre" sans rien donner. La bonne direction est: dire ce qui manque, puis exposer les elements valides disponibles.

## 3. Repo, branche et etat git

Repo local:

```text
C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG
```

Branche:

```text
SAAIA_V3.1
```

HEAD au moment du checkpoint:

```text
d2797b80 Improve source-backed planning orchestration
```

Remote:

```text
origin https://github.com/maximebirchler/SAAIA-RAG.git
```

Etat git:

```text
 M backend/SAAIA.Backend.Tests/ContextualTextProjectorTests.cs
 M backend/SAAIA.Backend.Tests/DocumentFoundationIntegrationTests.cs
 M backend/SAAIA.Backend.Tests/RetrievalRuntimeSwitchTests.cs
 M backend/SAAIA.Backend/Endpoints/RagEndpoints.cs
 M backend/SAAIA.Backend/Pdf/ContextualTextProjector.cs
 M client/SAAIA.Client.ToolAgent.Tests/LiveCuisineAgentValidationTests.cs
 M client/SAAIA.Client.ToolAgent.Tests/RagContextBudgetRegressionTests.cs
 M client/SAAIA.Client.ToolAgent.Tests/SourceBackedEvidencePlannerObservabilityTests.cs
 M client/SAAIA.Client.ToolAgent.Tests/StructuredPlanningCoverageTests.cs
 M client/SAAIA.Client.ToolAgent.Tests/ToolRouterPlanNormalizationTests.cs
 M client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs
 M client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.TestHooks.cs
 M client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.cs
```

Important:

- Le worktree etait deja sale avant ce checkpoint. Ne pas supposer que toutes les modifications listess ci-dessus viennent de cette reprise.
- Les fichiers touches directement dans ce checkpoint immediat sont surtout:
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs`
  - `client/SAAIA.Client.ToolAgent.Tests/StructuredPlanningCoverageTests.cs`
- `git diff --check` sur ces deux fichiers est OK. Il reste seulement des avertissements CRLF deja visibles sur plusieurs fichiers.
- Aucun commit/push n'a ete fait pendant ce checkpoint.

Diff numstat au moment du rapport:

```text
4     1     backend/SAAIA.Backend.Tests/ContextualTextProjectorTests.cs
1     1     backend/SAAIA.Backend.Tests/DocumentFoundationIntegrationTests.cs
46    2     backend/SAAIA.Backend.Tests/RetrievalRuntimeSwitchTests.cs
60    1     backend/SAAIA.Backend/Endpoints/RagEndpoints.cs
13    13    backend/SAAIA.Backend/Pdf/ContextualTextProjector.cs
133   7     client/SAAIA.Client.ToolAgent.Tests/LiveCuisineAgentValidationTests.cs
17    0     client/SAAIA.Client.ToolAgent.Tests/RagContextBudgetRegressionTests.cs
3     0     client/SAAIA.Client.ToolAgent.Tests/SourceBackedEvidencePlannerObservabilityTests.cs
2020  117   client/SAAIA.Client.ToolAgent.Tests/StructuredPlanningCoverageTests.cs
3     1     client/SAAIA.Client.ToolAgent.Tests/ToolRouterPlanNormalizationTests.cs
2699  200   client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs
29    2     client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.TestHooks.cs
243   40    client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.cs
```

## 4. Acces serveur et scripts prod

Le deploiement serveur doit se faire avec les scripts du repo, pas en lancant Docker localement pour remplacer le serveur.

Script principal trouve:

```text
infra/scripts/prod/deploy-remote-backend.ps1
```

Parametres par defaut du script:

```text
Server           = maxime@100.80.213.61
ContextName      = saaia-server
ComposeFile      = infra/docker-compose.prod.yml
EnvFile          = infra/.env.server-linux
DeployWorkDirRel = out/remote-deploy
```

Commandes utiles:

```powershell
# Deploiement backend remote, sans deps par defaut
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\deploy-remote-backend.ps1

# Deploiement avec dependances backend + postgres + qdrant + tei
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\deploy-remote-backend.ps1 -WithDependencies

# Rebuild backend remote sans cache
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\deploy-remote-backend.ps1 -NoCache

# Status compose remote
docker --context saaia-server compose -f infra/docker-compose.prod.yml --env-file infra/.env.server-linux ps

# Tunnel si le backend est lie a 127.0.0.1 cote serveur
ssh -L 5122:127.0.0.1:5122 maxime@100.80.213.61
```

Le script:

- verifie `docker`, `ssh`, `scp`, `dotnet`;
- provisionne les dossiers distants;
- genere et signe `deployment.config.json`;
- copie `deployment.config.json` et `deployment.config.sig`;
- cree/utilise le contexte Docker `saaia-server`;
- lance `docker --context saaia-server compose ... up -d --build`;
- verifie `/ready` sur le backend.

Ne pas afficher ni copier les secrets de `infra/.env.server-linux`. Le rapport donne les chemins et commandes, pas les valeurs secretes.

## 5. Script Telegram

Chemin exact:

```text
C:\Users\MBirchler\Desktop\ecom\SAAIA\SAAIA - Notifier\notify-codex.ps1
```

Script CMD associe:

```text
C:\Users\MBirchler\Desktop\ecom\SAAIA\SAAIA - Notifier\notify-codex.cmd
```

Variables d'environnement requises par le notifier:

```text
TELEGRAM_BOT_TOKEN
TELEGRAM_CHAT_ID
```

Utilisation fiable recommandee:

```powershell
@'
Corps detaille de notification.
'@ | & 'C:\Users\MBirchler\Desktop\ecom\SAAIA\SAAIA - Notifier\notify-codex.ps1' `
  -ReadStdin `
  -RequireMessage `
  -Title 'Titre notification'
```

Le script ajoute automatiquement:

```text
Machine: <COMPUTERNAME>
Dossier: <cwd>
```

Il decoupe automatiquement les messages longs en plusieurs parties. Pendant ce checkpoint, une notification de 4703 caracteres a ete envoyee en 2 parties.

## 6. Probleme principal trouve pendant ce checkpoint

Le systeme donnait parfois l'impression que le retrieval ou le LLM manquait de bonnes sources alors que des candidats utiles etaient presents. Plusieurs filtres deterministes coupaient les candidats avant que la selection finale puisse faire son travail.

Les causes trouvees:

1. Le chemin primaire faisait `Take(maxItems)` trop tot.
   - Avant: un titre court pouvait gagner le quota avant que le titre complet de la meme page soit compare.
   - Effet: le titre complet disparaissait avant la deduplication partielle.
   - Correction: classer les candidats, supprimer les titres partiels, puis appliquer le quota.

2. Les titres issus de `documents.context` etaient parfois rejetes alors qu'ils etaient legitimes.
   - Exemple de test: titre complet dans `sectionTitle` et `matchedContentCards`, mais texte de page commencant par un titre plus court.
   - Correction: accepter un titre contexte ancre quand il est visible dans les surfaces contextuelles/cartes et que la page a une preuve locale structuree, meme si la preuve est fuzzy et non une chaine exacte complete.

3. Le fallback de titres stricts repassait par un filtre bruit/noisy trop strict.
   - Un bypass avait ete ajoute dans l'extraction stricte, mais la boucle fallback rejetait ensuite le meme titre.
   - Correction: appliquer le meme bypass generique `SourceBackedContextTitleCanBypassNoisyStructuredPlanningTitle(...)` dans le fallback.

4. Le follow-up sommaire etait vide pour un cas valide.
   - Le parsing de `sourcePage` et `docPath` etait correct.
   - Le probleme venait d'un filtre anti-bruit trop large.
   - Exemple: `Soupe froide concombre` etait classe comme bruit parce que `concombre` finit par `re`.
   - Correction: remplacer la regex brute par `LooksLikeEmbeddedOperationalActionFragment(...)`, qui garde les vrais fragments d'action mais ne rejette plus un titre nominal court seulement a cause d'une terminaison.

## 7. Corrections code importantes

Fichier principal:

```text
client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs
```

Corrections a connaitre:

- `ComputeSourceBackedPlanningCandidateRankScore(...)`
  - booste les candidats `documents.context` avec preuve fuzzy ancree.

- `HasStrictStructuredPlanningCandidateEvidence(...)`
  - peut accepter une preuve contexte ancree au lieu d'exiger seulement la forme concrete stricte classique.

- `SourceBackedContextCandidateHasAnchoredFuzzyLocalStructuredProof(...)`
  - nouveau garde-fou generique: le hit doit venir de `documents.context`, le titre doit etre ancre dans les surfaces contexte/carte, et la page doit soutenir localement le candidat.

- `SourceBackedContextTitleCanBypassNoisyStructuredPlanningTitle(...)`
  - permet de traverser le filtre noisy uniquement si l'ancrage contexte + preuve locale sont solides.

- `PrimaryPageEvidenceSupportsClippedStructuredPlanningTitle(...)`
  - traite le cas ou la page contient une version courte/clippee d'un titre complet, mais suffisamment de termes/proof locaux.

- `SingleTermCandidateLooksLikeLooseContextLabel(...)`
  - evite qu'un label contexte d'un seul mot, par exemple un pays ou un contexte general, soit pris comme candidat final s'il flotte autour d'un vrai item.

- `LooksLikeEmbeddedOperationalActionFragment(...)`
  - remplace une regex trop large qui confondait noms courts et fragments d'instructions.

- Selection primaire:
  - variable `rankedPrimaryCandidates`;
  - `RemoveSourceBackedPlanningPartialTitleDuplicates(...)` est appele avant `Take(maxItems)`.

- Diagnostics permanents ajoutes dans les traces de candidat:
  - `anchored_context_proof`
  - `usable_candidate`
  - `page_context_label`
  - `field_label`
  - `supporting_field`
  - `embedded_field`
  - `noisy_title`
  - `procedure_title`

Ces balises sont importantes pour voir pourquoi un candidat est accepte/rejete et pour eviter l'impression que le systeme est bloque ailleurs.

## 8. Tests ajoutes/modifies directement pour ce checkpoint

Fichier:

```text
client/SAAIA.Client.ToolAgent.Tests/StructuredPlanningCoverageTests.cs
```

Tests importants:

- `Structured_planning_rejects_loose_single_term_context_title_but_keeps_body_candidate`
  - verifie qu'un label contexte faible d'un seul mot ne gagne pas contre un vrai candidat de corps.

- `Structured_planning_accepts_document_context_title_with_fuzzy_local_proof_after_navigation_read`
  - verifie qu'un titre complet issu de `documents.context` est conserve si la page prouve localement le contenu, meme quand le texte commence par un titre plus court.

- `Structured_planning_summary_followup_preserves_page_metadata_for_bounded_search`
  - verifie que `summary.search` conserve `docPath` + `sourcePage` et genere des requetes titre + page.

- `Structured_planning_noisy_title_filter_keeps_short_noun_titles_ending_like_verbs`
  - nouveau test de regression.
  - verifie que `Valve pressure enclosure` et `Soupe froide concombre` ne sont pas rejetes comme bruit juste a cause d'une terminaison type verbe.

## 9. Validations executees et resultats

Commandes executees depuis:

```text
C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG
```

### 9.1 Trois regressions initiales

Commande:

```powershell
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Structured_planning_rejects_loose_single_term_context_title_but_keeps_body_candidate|FullyQualifiedName~Structured_planning_accepts_document_context_title_with_fuzzy_local_proof_after_navigation_read|FullyQualifiedName~Structured_planning_summary_followup_preserves_page_metadata_for_bounded_search" --logger "console;verbosity=normal"
```

Resultat:

```text
Total tests: 3
Passed: 3
```

### 9.2 Lot navigation/contexte/sommaire

Commande:

```powershell
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Structured_planning_context_followup|FullyQualifiedName~Structured_planning_document_navigation_followup|FullyQualifiedName~Structured_meal_planning_navigation_followup|FullyQualifiedName~Structured_planning_summary_followup|FullyQualifiedName~Structured_planning_uses_document_context_item_metadata|FullyQualifiedName~Structured_planning_accepts_document_context_title|FullyQualifiedName~Structured_planning_rejects_loose_single_term_context_title|FullyQualifiedName~Structured_planning_accepts_context_title_when" --logger "console;verbosity=normal"
```

Resultat:

```text
Total tests: 17
Passed: 17
```

Tests passes inclus:

- `Structured_meal_planning_navigation_followup_skips_candidate_titles_already_seen_in_rag_results`
- `Structured_planning_context_followup_reads_noisy_retrieved_page_anchors`
- `Structured_meal_planning_navigation_followup_skips_observed_non_recipe_anchors`
- `Structured_planning_document_navigation_followup_rejects_subjectless_standard_reference_anchors`
- `Structured_planning_uses_document_context_item_metadata_as_candidate_evidence`
- `Structured_planning_rejects_loose_single_term_context_title_but_keeps_body_candidate`
- `Structured_planning_accepts_document_context_title_with_fuzzy_local_proof_after_navigation_read`
- `Structured_planning_summary_followup_preserves_page_metadata_for_bounded_search`
- `Structured_planning_accepts_context_title_when_local_structure_uses_ingredients_label`
- `Structured_meal_planning_navigation_followup_skips_ocr_organization_anchor_and_keeps_recipe_title`
- `Structured_planning_context_followup_stays_inside_dominant_top_level_scope`
- `Structured_planning_context_followup_deduplicates_same_document_page_anchors`
- `Structured_planning_context_followup_skips_already_read_table_of_contents_pages`
- `Structured_planning_document_navigation_followup_uses_older_page_anchors`
- `Structured_planning_context_followup_reads_retrieved_doc_page_anchors`
- `Structured_planning_context_followup_reads_table_of_contents_navigation_anchors`
- `Structured_meal_planning_navigation_followup_prioritizes_complete_title_over_contained_fragment`

### 9.3 Lot bruit/action/titres nominaux

Commande:

```powershell
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --filter "FullyQualifiedName~Structured_meal_planning_rejects_observed_live_action_and_field_fragments|FullyQualifiedName~Structured_meal_planning_rejects_storage_or_conservation_fragments_as_candidates|FullyQualifiedName~Structured_planning_noisy_title_filter_keeps_short_noun_titles_ending_like_verbs|FullyQualifiedName~Structured_planning_uses_body_candidate_when_card_title_is_noisy_action_fragment|FullyQualifiedName~Structured_meal_planning_rejects_observed_action_step_titles_as_candidates" --logger "console;verbosity=normal"
```

Resultat:

```text
Total tests: 5
Passed: 5
```

### 9.4 Lot preuves locales / cartes / index / titres courts

Commande:

```powershell
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Structured_planning_accepts_clipped_card_title|FullyQualifiedName~Structured_planning_accepts_paged_card|FullyQualifiedName~Structured_planning_accepts_self_contained_card|FullyQualifiedName~Structured_planning_rejects_explicit_paged_card|FullyQualifiedName~Structured_planning_rejects_floating_card|FullyQualifiedName~Structured_planning_accepts_recipe_card_when_local_proof|FullyQualifiedName~Structured_planning_accepts_exact_title_with_dense|FullyQualifiedName~Structured_planning_accepts_ocr_split|FullyQualifiedName~Structured_planning_rejects_field_value|FullyQualifiedName~Structured_planning_rejects_supporting_field|FullyQualifiedName~Structured_planning_does_not_validate|FullyQualifiedName~Structured_planning_does_not_treat_card_title|FullyQualifiedName~Structured_planning_rejects_unattached|FullyQualifiedName~Structured_planning_keeps_short_title|FullyQualifiedName~Structured_planning_extracts_multiple_local|FullyQualifiedName~Structured_planning_accepts_self_contained_content_card" --logger "console;verbosity=normal"
```

Resultat:

```text
Total tests: 19
Passed: 19
```

### 9.5 Tests finaux plan de repas critiques

Tous les tests ci-dessous ont ete lances individuellement ou par deux parce qu'ils sont lents.

Passes:

```text
Final_ui_weekly_meal_plan_keeps_only_useful_distinct_cited_sources
Weekly_meal_plan_deterministic_gate_rejects_wrong_slot_fillers_and_uses_better_options_when_sufficient
Final_ui_weekly_meal_plan_replaces_writer_answer_with_wrong_slot_fillers
Final_ui_weekly_meal_plan_rotates_sourced_slot_candidates_after_filtered_shortage
Final_ui_weekly_meal_plan_does_not_rotate_when_user_requests_no_duplicate_sources
Weekly_meal_plan_assigns_sources_to_compatible_slots_and_skips_non_meal_titles
Weekly_meal_plan_route_backfills_distinct_breakfast_candidate_without_recycling_sources
Weekly_meal_plan_uses_retrieval_query_routes_to_fill_observed_recipe_slots
Weekly_meal_plan_preserves_slot_route_when_duplicate_candidate_is_found_through_multiple_queries
```

Temps observes:

```text
Final_ui_weekly_meal_plan_keeps_only_useful_distinct_cited_sources: environ 1 min 1 s
Weekly_meal_plan_deterministic_gate_rejects_wrong_slot_fillers_and_uses_better_options_when_sufficient: environ 2 min 36 s
Final_ui_weekly_meal_plan_replaces_writer_answer_with_wrong_slot_fillers: environ 1 min 33 s
Final_ui_weekly_meal_plan_rotates_sourced_slot_candidates_after_filtered_shortage + no duplicate sources: environ 3 min 54 s pour les deux
Weekly_meal_plan_assigns_sources_to_compatible_slots_and_skips_non_meal_titles: environ 1 min 55 s
Weekly_meal_plan_route_backfills_distinct_breakfast_candidate_without_recycling_sources: environ 3 min 25 s
Weekly_meal_plan_uses_retrieval_query_routes_to_fill_observed_recipe_slots: environ 1 min 57 s
Weekly_meal_plan_preserves_slot_route_when_duplicate_candidate_is_found_through_multiple_queries: environ 1 min 30 s
```

### 9.6 Tests meal planning restants lances apres creation du rapport

Apres la premiere version de ce rapport, les tests restants listes en prochaine etape ont ete lances individuellement. Ils passent tous:

```text
Weekly_meal_plan_accepts_light_snacks_from_neutral_routes_without_promoting_standalone_dips
Weekly_meal_plan_writer_inventory_exposes_route_and_slot_fit_for_llm_adjudication
Weekly_meal_plan_writer_inventory_keeps_source_pages_when_candidate_extraction_is_sparse
Weekly_meal_plan_candidate_adjudication_prompt_exposes_routes_sources_and_json_contract
Weekly_meal_plan_with_useful_partial_recipe_coverage_returns_sourced_candidate_bank
Vague_weekly_meal_plan_writer_guard_uses_raw_adequate_research_over_truncated_writer_context
```

Resultat:

```text
Total tests individuels supplementaires: 6
Passed: 6
```

Points couverts:

- collation/snack depuis route neutre sans promouvoir de mauvais elements principaux;
- inventaire writer avec routes, sources et slot-fit pour adjudication LLM;
- conservation des pages sources quand l'extraction candidat est sparse;
- prompt d'adjudication avec routes, sources et contrat JSON;
- fallback candidate bank avec sources partielles utiles;
- garde vague qui utilise la recherche brute adequate plutot qu'un contexte writer tronque.

## 10. Tests trop longs / non conclusifs

Ces tests/lots n'ont pas donne de verdict exploitable dans le timeout outil:

1. Classe complete:

```powershell
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter FullyQualifiedName~StructuredPlanningCoverageTests --logger "console;verbosity=normal"
```

Elle a depasse 604 s, puis le process etait encore actif apres attente supplementaire. Le runner a ete arrete proprement.

2. Grand lot weekly/structured meal planning:

```powershell
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Weekly_meal_plan|FullyQualifiedName~Structured_meal_planning|FullyQualifiedName~Explicit_weekly_meal_plan|FullyQualifiedName~Vague_weekly_meal_plan" --logger "console;verbosity=normal"
```

Timeout apres environ 424 s. Runner arrete proprement.

3. Sous-lot final trop large:

```powershell
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Final_ui_weekly_meal_plan|FullyQualifiedName~Weekly_meal_plan_deterministic_gate|FullyQualifiedName~Weekly_meal_plan_assigns_sources|FullyQualifiedName~Weekly_meal_plan_with_useful_partial_recipe_coverage" --logger "console;verbosity=normal"
```

Timeout apres environ 244 s. Runner arrete proprement.

Conclusion:

- Les tests individuels critiques passent.
- Les grands lots sont trop longs pour etre utilises tels quels dans cet environnement.
- Il faut continuer la validation en sous-lots ou augmenter les timeouts si besoin.
- Il y a peut-etre un sujet performance test a traiter plus tard, car plusieurs tests individuels prennent 1 a 4 minutes.

## 11. Verification anti hardcode cuisine

Grep cible sur le code principal:

```powershell
rg -n "badigeonne|tarte|houmous|catalane|soupe|concombre|recette|recipes?|cuisine|meal|repas|gouter|collation|souper|diner|petit-dejeuner" client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.cs client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.TestHooks.cs
```

Resultats pertinents dans le code principal:

```text
ToolAgentOrchestrator.State.cs: regex generique avec recipes?/recettes? dans un filtre de titres inventaire generique.
ToolAgentOrchestrator.State.cs: "starten" dans une liste de labels multilingues, faux positif sur "tarte".
ToolAgentOrchestrator.State.cs: regex marketing "getting started", faux positif sur "tarte".
```

Ce grep cible ne montre pas de nouveau durcissement du type:

```text
badigeonne
tarte
houmous
catalane
soupe
concombre
```

Attention:

- Ce n'est pas un audit exhaustif du repo complet.
- Les tests contiennent beaucoup d'exemples cuisine, volontairement, car le scenario final est un plan de repas.
- Il reste a faire un audit plus large et plus propre des elements indesirables codes en dur, surtout dans les tests, le router et les helpers de validation.

## 12. Ce qui fonctionne mieux maintenant

- Le systeme sait mieux conserver un titre complet venant du contexte quand le chunk/page montre une preuve locale suffisante.
- Le systeme evite mieux de preferer un titre court tronque quand une variante complete de la meme page existe.
- Les sommaires peuvent redevenir un outil utile: `summary.search` avec `sourcePage` peut produire une recherche bornee dans le bon document et la bonne page.
- Les faux positifs de bruit sur titres courts sont reduits.
- Les traces de candidat exposent mieux les raisons d'acceptation/rejet, ce qui permet de suivre ce qui se passe sans deviner.
- Les tests critiques autour des mauvais slots du plan de repas passent.

## 13. Ce qui ne fonctionne pas encore / reste a prouver

1. Le scenario UI reel final n'a pas ete relance dans cette reprise.
   - Les tests unitaires/integration client critiques passent.
   - Il faut encore refaire un essai UI reel avec la phrase finale exacte et observer reponse + sources.

2. Les grands lots de tests sont trop longs.
   - Il faut soit les couper en suites plus petites, soit accepter des timeouts plus grands, soit optimiser les tests.

3. Le chantier ingestion/OCR/indexation n'est pas declare parfait.
   - Ce rapport ne dit pas que l'ingestion/OCR est terminee.
   - Il faut continuer a verifier les chunks reels, les titres, les sommaires, les documents anglais/autres langues, les PDF scans et les images OCR.

4. Il reste un audit hardcode generique a faire.
   - Le grep cible code principal est rassurant sur les termes mentionnes par l'utilisateur.
   - Mais le repo a beaucoup de tests et helpers historiques autour cuisine.
   - Il faut s'assurer que le code produit reste generique et que les tests cuisine ne deviennent pas des regles produit invisibles.

5. Le repo n'est pas pret a commit/push sans revue finale.
   - Worktree sale avec 13 fichiers modifies.
   - Certains changements backend/ingestion viennent d'un chantier precedent.
   - Ne pas commit a l'aveugle sans separer ou au minimum relire le diff complet.

## 14. Prochaine suite recommandee

Ordre conseille:

1. Relancer un essai UI reel de la question finale.
   - Ne pas utiliser computer-use sauf besoin.
   - Si l'utilisateur lance lui-meme l'UI, lui donner la question exacte.
   - Si Codex doit lancer l'UI, utiliser computer-use seulement le temps necessaire et relacher le controle ensuite.

2. Lire la reponse finale:
   - verifier que gouter/collation ne sont pas traites comme deux slots differents;
   - verifier que les repas principaux ne recoivent pas des desserts ou dips faibles;
   - verifier que les sources sont utiles, non dupliquees inutilement, et reellement liees aux items;
   - verifier que le systeme dit clairement si la couverture est partielle.

3. Apres validation retrieval/planning, revenir au chantier ingestion/OCR/indexation.
   - Verifier chunks reels par categorie.
   - Verifier sommaires comme chunks speciaux de document quand disponibles.
   - Verifier OCR sur PDF scans/images.
   - Verifier refusion des mots coupes, headers/footers/pages, paragraphes, titres, tableaux/listes/adresses.
   - Verifier documents anglais/autres langues.

4. Nettoyage repo avant commit:
   - `git diff --stat`
   - `git diff --check`
   - revue des fichiers backend vs client
   - supprimer tout artefact temporaire
   - verifier qu'aucun process test/dotnet parasite ne tourne
   - commit atomique si possible, sinon commit large mais documente
   - push seulement apres accord ou quand le checkpoint est vraiment stable.

## 15. Commandes utiles de reprise

Verifier branche/commit:

```powershell
git branch --show-current
git rev-parse --short HEAD
git log -1 --oneline
```

Verifier worktree:

```powershell
git status --short
git diff --stat
git diff --numstat
git diff --check
```

Verifier qu'il n'y a pas de test reste actif:

```powershell
Get-CimInstance Win32_Process -Filter "name = 'dotnet.exe'" | Select-Object ProcessId,CreationDate,CommandLine | Format-List
Get-Process testhost -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,CPU,StartTime | Format-Table -AutoSize
```

Relancer les 3 regressions principales:

```powershell
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Structured_planning_rejects_loose_single_term_context_title_but_keeps_body_candidate|FullyQualifiedName~Structured_planning_accepts_document_context_title_with_fuzzy_local_proof_after_navigation_read|FullyQualifiedName~Structured_planning_summary_followup_preserves_page_metadata_for_bounded_search" --logger "console;verbosity=normal"
```

Relancer le test final sources utiles:

```powershell
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter FullyQualifiedName~Final_ui_weekly_meal_plan_keeps_only_useful_distinct_cited_sources --logger "console;verbosity=normal"
```

Envoyer une notification Telegram:

```powershell
@'
Message detaille.
'@ | & 'C:\Users\MBirchler\Desktop\ecom\SAAIA\SAAIA - Notifier\notify-codex.ps1' -ReadStdin -RequireMessage -Title 'SAAIA - reprise'
```

Deployer backend remote:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\deploy-remote-backend.ps1
```

## 16. Notes de vigilance pour le prochain agent

- Ne pas utiliser Docker local pour remplacer le backend serveur. Le serveur cible est dans `deploy-remote-backend.ps1`.
- Ne pas utiliser le LLM serveur pour repondre a la place du client.
- Ne pas traiter `gouter` et `collation` comme deux slots differents: ce sont deux mots pour le meme slot.
- Ne pas ajouter de logique cuisine en dur pour corriger un exemple de plan de repas.
- Ne pas faire confiance aux titres seuls: un titre peut etre faible, incomplet ou non representatif du chunk.
- Ne pas rejeter un chunk uniquement parce que le titre semble moyen si le contenu local est une bonne preuve.
- Toujours regarder contenu + cartes + page + route de retrieval + source avant de juger un candidat.
- Les grands tests sont lents; preferer les filtres individuels pour obtenir des verdicts exploitables.
- Les traces lisibles comptent autant que le resultat: l'utilisateur veut savoir si le systeme travaille vraiment ou s'il semble bloque.
