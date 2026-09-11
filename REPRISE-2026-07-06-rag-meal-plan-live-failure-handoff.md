# Rapport de reprise - RAG meal-plan live failure

Date de redaction: 2026-07-06, Europe/Zurich.
Repo local: `C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG`.
Branche: `SAAIA_V3.1`.

## Decision de stop

On s'arrete ici proprement.

Je ne considere pas que la question finale du plan de repas soit "tres proche" d'etre validee. Les tests unitaires / deterministes ont beaucoup progresse, mais le dernier test live reel echoue encore sur un fallback. Il ne faut pas continuer a empiler des patchs en donnant l'impression que le dernier metre suffit: le chemin live montre une derive de recherche et de selection de candidats qui doit etre traitee explicitement.

## Question finale de validation

Question utilisee par le test live:

```text
J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi en y mettant petit-dejeuner, diner, souper et gouter / collation chaque jour. Fais un format clair et user-friendly, avec uniquement des sources utiles, non dupliquees inutilement. N'invente rien.
```

Important: `gouter` et `collation` doivent etre compris comme la meme intention. Il ne faut pas creer deux slots differents. Le LLM client doit pouvoir comprendre l'equivalence, et le code de support ne doit pas durcir une logique specifique cuisine.

## Dernier resultat live

Artefact du dernier test live:

```text
artifacts/live-validation/live-final-weekly-meal-plan-after-page-proof-20260706.log
```

Commande executee:

```powershell
New-Item -ItemType Directory -Force artifacts/live-validation | Out-Null
$env:SAAIA_LIVE_VALIDATION='1'
$env:SAAIA_LIVE_FINAL_TIMEOUT_MINUTES='24'
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --filter "Live_final_weekly_meal_plan_question_runs_through_real_client_agent_when_enabled" --no-restore --no-build --logger "console;verbosity=normal" *> artifacts/live-validation/live-final-weekly-meal-plan-after-page-proof-20260706.log
exit $LASTEXITCODE
```

Resultat:

```text
Exit code: 1
Total time: 13,4742 Minutes
SOURCE_COUNT: 8
Assertion failure: expected "Lundi", answer starts with fallback text.
```

Reponse produite par le live:

```text
Les sources recuperees couvrent seulement une partie de la structure demandee, pas assez pour la completer sans repetitions.
Elements directement utilisables :
- Josiane dejpetits A josiane dej
- Yorkshire pudding
- 1 fruit
- Allemagne
- Petits haricots blancs con fits A la menthe
- Creme de petit pois et d' a vocat
- Serts etpetits
- Spirales de courgette
Pour completer le planning proprement, il faut d'autres elements confirmes par les documents.
Je peux elargir la recherche pour essayer de trouver des sources plus adaptees.
```

Diagnostics du live:

```text
intent=rag.answer
tools=rag.multi_search,documents.tree,documents.navigation,documents.context
ragQueries=Diner co | Diner co page 10 | Diner co p 10 | Diner co pages 10-11 | Diner co p 10-11 | Diner co contenu | Diner co details | Diner co etapes
sources=Je_cuisine_simplement.pdf | nobilia-recettes-internationales-FR.pdf | livre-recette-sist-2025-web.pdf | nobilia-recettes-internationales-FR.pdf | chefbot_livre_de_recettes_fr.pdf | chefbot_livre_de_recettes_fr.pdf | chefbot_livre_de_recettes_fr.pdf | chefbot_livre_de_recettes_fr.pdf
```

Interpretation importante:

- Le live utilise bien plusieurs outils: `rag.multi_search`, `documents.tree`, `documents.navigation`, `documents.context`.
- Il y a donc une exploration, mais elle derive vers une requete etroite et mauvaise: `Diner co`.
- Le probleme n'est pas seulement "pas assez de sources". Le pipeline se laisse encore contaminer par un mauvais libelle / fragment et construit ensuite des recherches autour de ce fragment.
- La reponse finale est un fallback, pas un planning semaine lundi-vendredi.
- Il reste des candidats invalides ou trop faibles: `1 fruit`, `Allemagne`, `Serts etpetits`, `Josiane dejpetits A josiane dej`.
- Des candidats meilleurs existent dans la meme reponse: `Petits haricots blancs...`, `Creme de petit pois...`, `Spirales de courgette`, possiblement `Yorkshire pudding`, mais le systeme ne les transforme pas en plan fiable.

## Ce qui a ete ameliore

### Backend / ingestion / OCR / fondation documentaire

Fichiers modifies:

```text
backend/SAAIA.Backend/Pdf/ContextualTextProjector.cs
backend/SAAIA.Backend/Endpoints/RagEndpoints.cs
backend/SAAIA.Backend.Tests/ContextualTextProjectorTests.cs
backend/SAAIA.Backend.Tests/DocumentFoundationIntegrationTests.cs
backend/SAAIA.Backend.Tests/RetrievalRuntimeSwitchTests.cs
```

Travail realise:

- Correction de certains cas ou le texte OCR/page visible etait trop vite considere comme bruit de navigation.
- Conservation de preuves page-locales quand elles contiennent une structure exploitable.
- Ajout/ajustement de tests sur la fondation documentaire et les switches runtime retrieval.
- Attention maintenue sur le fait que les corrections doivent rester generiques, pas codees pour la cuisine.

Etat important:

- Le chantier ingestion/OCR n'est pas declare parfait.
- Il a progresse, mais le live final montre que la qualite effective des candidats et des libelles reste insuffisante.
- Il ne faut pas dire que l'ingestion/indexation/OCR est finie "parfaitement".

### Client / ToolAgent / planning source-backed

Fichiers modifies:

```text
client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs
client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.cs
client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.TestHooks.cs
client/SAAIA.Client.ToolAgent.Tests/StructuredPlanningCoverageTests.cs
client/SAAIA.Client.ToolAgent.Tests/LiveCuisineAgentValidationTests.cs
client/SAAIA.Client.ToolAgent.Tests/RagContextBudgetRegressionTests.cs
client/SAAIA.Client.ToolAgent.Tests/SourceBackedEvidencePlannerObservabilityTests.cs
client/SAAIA.Client.ToolAgent.Tests/ToolRouterPlanNormalizationTests.cs
```

Travail realise:

- Ajout d'observabilite et de traces sur le planning source-backed.
- Renforcement de la detection de mauvais titres/candidats issus de fragments OCR ou de champs.
- Correction de plusieurs faux candidats observes dans les essais precedents:
  - `Poivre fraichement moulu`
  - `TA LASAGNE PLATS PRINCI- PAUX`
  - `KAKILES A-COTES`
  - fragments descriptifs et valeurs de champs pris comme titres.
- Ajout de tests pour rejeter les field-values et les faux titres enrichis.
- Ajout d'un test pour accepter une carte OCR page-prefixed quand le titre est scinde mais que le corps contient une structure suffisante.
- Ajustement de la logique de normalisation des titres de cartes de contenu pour ne pas prendre des termes ponts generiques (`Ingredients`, `Preparation`, `Components`, etc.) comme des titres concurrents.

Points de code a inspecter en priorite a la reprise:

```text
client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs:15870
client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs:15974
client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs:17581
client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs:18352
client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs:21740
client/SAAIA.Client.ToolAgent.Tests/StructuredPlanningCoverageTests.cs:4295
```

Symboles principaux:

```text
PrimaryEvidenceHasConcreteStructuredPlanningCues
LooksLikePageLocatorNavigationOnlySurface
LooksLikeStructuredPlanningFieldValueCandidate
NormalizeCheapContentCardEvidenceItemTitle
IsGenericStructuredPlanningEvidenceBridgeTerm
Structured_planning_accepts_page_prefixed_ocr_card_when_title_is_split_but_body_is_structured
```

## Tests valides

Build:

```powershell
dotnet build client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity minimal
```

Resultat: OK.

Tests sensibles passes individuellement:

```text
Structured_planning_accepts_page_prefixed_ocr_card_when_title_is_split_but_body_is_structured
Structured_planning_accepts_page_anchored_card_title_when_body_proof_is_supported_without_title_echo
Structured_planning_rejects_explicit_paged_card_evidence_when_it_is_only_index_navigation
Structured_planning_rejects_floating_card_title_without_page_local_proof
Structured_planning_rejects_primary_text_field_value_even_without_content_card
Structured_planning_rejects_field_value_even_when_page_has_dense_proof
```

Groupe de tests planning sensibles passes individuellement:

```text
Structured_meal_planning_keeps_backend_like_recipe_cards_and_drops_partial_duplicates
Structured_meal_planning_cleans_live_ocr_context_suffixes_and_field_fragments
Structured_planning_rejects_field_value_even_when_page_has_dense_proof
Structured_planning_rejects_field_value_even_when_card_prepends_false_section_title
Structured_planning_rejects_enriched_field_value_card_when_title_is_only_inside_field_list
Structured_planning_rejects_descriptive_sentence_fragments_from_card_titles
Structured_planning_rejects_supporting_field_values_after_context_navigation_read
Structured_meal_planning_rejects_observed_live_action_and_field_fragments
Structured_planning_noisy_title_filter_keeps_short_noun_titles_ending_like_verbs
Structured_planning_accepts_page_prefixed_ocr_card_when_title_is_split_but_body_is_structured
```

Tests finaux semaine passes individuellement:

```text
Final_ui_weekly_meal_plan_keeps_only_useful_distinct_cited_sources
Final_ui_weekly_meal_plan_does_not_rotate_when_user_requests_no_duplicate_sources
Final_ui_weekly_meal_plan_replaces_writer_answer_with_wrong_slot_fillers
Weekly_meal_plan_with_weekdays_supper_and_snack_builds_structured_user_friendly_grid
Weekly_meal_plan_assigns_sources_to_compatible_slots_and_skips_non_meal_titles
Weekly_meal_plan_does_not_fill_missing_breakfast_slots_with_desserts_or_main_dishes
Weekly_meal_plan_uses_retrieval_query_routes_to_fill_observed_recipe_slots
```

Attention:

- Le groupe combine des tests finaux semaine a deja timeout vers 244 s.
- Chaque test individuel passe, donc c'est probablement un probleme de cout/duree de suite plus qu'un echec fonctionnel deterministe.
- Le test live reel echoue toujours.

## Etat git actuel

Commande:

```powershell
git status --short --branch
```

Etat:

```text
## SAAIA_V3.1...origin/SAAIA_V3.1
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
?? REPRISE-2026-07-06-rag-retrieval-planning-checkpoint.md
?? REPRISE-2026-07-06-rag-meal-plan-live-failure-handoff.md
```

Diff stat avant ajout de ce rapport:

```text
13 files changed, 7399 insertions(+), 1345 deletions(-)
```

Diff numstat avant ajout de ce rapport:

```text
4    1    backend/SAAIA.Backend.Tests/ContextualTextProjectorTests.cs
1    1    backend/SAAIA.Backend.Tests/DocumentFoundationIntegrationTests.cs
46   2    backend/SAAIA.Backend.Tests/RetrievalRuntimeSwitchTests.cs
60   1    backend/SAAIA.Backend/Endpoints/RagEndpoints.cs
13   13   backend/SAAIA.Backend/Pdf/ContextualTextProjector.cs
133  7    client/SAAIA.Client.ToolAgent.Tests/LiveCuisineAgentValidationTests.cs
17   0    client/SAAIA.Client.ToolAgent.Tests/RagContextBudgetRegressionTests.cs
3    0    client/SAAIA.Client.ToolAgent.Tests/SourceBackedEvidencePlannerObservabilityTests.cs
3320 920  client/SAAIA.Client.ToolAgent.Tests/StructuredPlanningCoverageTests.cs
3    1    client/SAAIA.Client.ToolAgent.Tests/ToolRouterPlanNormalizationTests.cs
3527 357  client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.State.cs
29   2    client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.TestHooks.cs
243  40   client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.cs
```

`git diff --check`:

```text
No hard whitespace errors reported.
Only CRLF warnings on some modified files.
```

Pas de commit ni push effectue a ce stade. Le repo reste volontairement sale car le live final n'est pas valide.

## Deploiement / serveur / contraintes d'execution

Ne pas lancer Docker localement pour ce chantier. Le backend utilise le serveur.

Acces serveur connu:

```text
SSH: maxime@100.80.213.61
Docker context: saaia-server
Compose file: infra/docker-compose.prod.yml
Env file: infra/.env.server-linux
```

Commandes utiles:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\deploy-remote-backend.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\deploy-remote-backend.ps1 -WithDependencies
docker --context saaia-server compose -f infra/docker-compose.prod.yml --env-file infra/.env.server-linux ps
ssh -L 5122:127.0.0.1:5122 maxime@100.80.213.61
```

Ne pas exposer les secrets contenus dans les fichiers `.env`.

## Notifications Telegram

Script:

```text
C:\Users\MBirchler\Desktop\ecom\SAAIA\SAAIA - Notifier\notify-codex.ps1
```

Commande type:

```powershell
@'
Message detaille et lisible.
'@ | & 'C:\Users\MBirchler\Desktop\ecom\SAAIA\SAAIA - Notifier\notify-codex.ps1' -ReadStdin -RequireMessage -Title 'Titre clair'
```

Preferences utilisateur:

- Mettre du detail comprehensible.
- Eviter les identifiants techniques incomprehensibles dans le corps, sauf quand ils servent vraiment a reprendre.
- Dire ce qui a ete trouve depuis la derniere notification, ce qui a ete fait, ce qui est teste, et le prochain verrou.

## Ce qui ne fonctionne toujours pas

### 1. La question finale live ne produit pas de plan

Le systeme doit produire un tableau / format clair lundi-vendredi avec petit-dejeuner, diner, souper et gouter/collation, avec sources utiles et non dupliquees inutilement.

Actuellement, le live produit un fallback indiquant que les sources ne suffisent pas.

### 2. La recherche live derive sur un fragment

Le diagnostic montre:

```text
ragQueries=Diner co | Diner co page 10 | Diner co p 10 | ...
```

Il faut trouver pourquoi le systeme transforme l'exploration en une serie de recherches autour de `Diner co`. C'est probablement le verrou numero 1.

Hypotheses:

- Un fragment OCR / titre partiel est promu trop haut.
- Le contexte de navigation ou de sommaire donne un label incomplet.
- La generation de follow-up queries prend un mauvais titre comme ancre principale.
- Le planner croit avoir une piste concrete alors que c'est une etiquette corrompue.

### 3. Les candidats invalides ne sont pas tous filtres

Exemples live encore presents:

```text
1 fruit
Allemagne
Serts etpetits
Josiane dejpetits A josiane dej
```

Ces exemples doivent etre traites de maniere generique:

- `1 fruit`: fragment quantite/aliment, pas un item autonome si aucune preuve de titre ou de structure autour.
- `Allemagne`: heading geographique ou categorie, pas un candidat final sauf si le corps prouve un item/action utilisable.
- `Serts etpetits`: concatenation OCR/section, probablement non self-contained.
- `Josiane dejpetits A josiane dej`: titre corrompu ou fusion de fragments, non acceptable comme source finale.

Ne pas coder "cuisine" en dur. Les regles doivent generaliser aux listes, documents techniques, contacts, procedures, tableaux, adresses, etc.

### 4. Le role exact du LLM client reste a clarifier dans le code

Philosophie utilisateur:

- Le LLM client est l'orchestrateur principal.
- Le code fournit des outils, des garde-fous, de l'observabilite et des contrats.
- Le code ne doit pas prendre toutes les decisions metier via heuristiques.
- Le LLM serveur est optionnel pour le moment. Le logiciel doit fonctionner sans lui.

Etat observe:

- Le code contient encore beaucoup de logique deterministe de compensation.
- Les tests prouvent certains comportements, mais le live montre que l'orchestration n'est pas encore assez robuste.
- La prochaine etape doit probablement recentrer le planner/outillage sur:
  - voir les categories/document tree/sommaires;
  - explorer plusieurs pistes;
  - valider la legitimite des sources;
  - refuser les fragments;
  - construire une couverture de slots avant d'ecrire;
  - continuer la recherche quand la couverture est insuffisante.

### 5. L'ingestion/indexation/OCR n'est pas encore declaree parfaite

Ce chantier a progresse, mais il reste des indices de mauvaise extraction:

- mots colles;
- titres corrompus;
- morceaux de sections melanges;
- OCR qui produit des titres ou fragments partiels;
- pages qui peuvent contenir assez d'information mais dont les chunks/cards remontent mal.

Il faudra revenir au chantier ingestion/indexation/OCR avec une campagne de verification plus systematique, mais le verrou immediat pour la question finale est maintenant la combinaison `retrieval exploration -> candidate legitimacy -> final planning coverage`.

## Suite conseillee

Ordre recommande:

1. Reproduire localement le live avec le meme artefact, sans relancer des changements larges.
2. Inspecter pourquoi `Diner co` devient l'ancre des recherches.
3. Ajouter un test minimal qui reproduit cette derive de query/follow-up.
4. Corriger la generation de follow-up pour interdire qu'un fragment corrompu devienne la requete principale quand la demande exige une couverture large.
5. Ajouter un test live-like qui simule les candidats invalides observes: `1 fruit`, `Allemagne`, `Serts etpetits`, `Josiane dejpetits...`.
6. Renforcer la validation generique de "source finale legitime" sans code dur cuisine.
7. Relancer les tests deterministes deja listes.
8. Relancer le live final.
9. Seulement si le live final produit un vrai plan lundi-vendredi avec sources propres: nettoyer, commit, push.

## Risques de reprise

- Ne pas confondre tests deterministes verts et validation produit reelle.
- Ne pas passer au chantier ingestion/OCR global en oubliant que le live final echoue encore sur l'orchestration/retrieval.
- Ne pas utiliser le LLM serveur comme substitut au LLM client orchestrateur.
- Ne pas faire de patch cuisine-specifique pour cacher `Allemagne`, `1 fruit`, etc.
- Ne pas commit/push avant que la question finale soit validee ou que le user accepte explicitement un checkpoint intermediaire.

## Resume court

Ce qui est mieux:

- Plusieurs faux candidats observes precedemment sont maintenant rejetes.
- Les tests deterministes de planning passent individuellement.
- Les preuves OCR/page-prefixed sont mieux gerees.
- Le live utilise bien plusieurs outils d'exploration.

Ce qui bloque:

- Le live final echoue encore.
- La recherche derive vers `Diner co`.
- Les candidats finaux contiennent encore des fragments ou headings invalides.
- Le systeme conclut qu'il n'a pas assez de sources alors que la base cuisine devrait en avoir largement.

Conclusion: arret propre ici. La reprise doit cibler la derive de requete et la legitimite des candidats avant de refaire un grand chantier.
