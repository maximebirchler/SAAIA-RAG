# Audit RAG source-backed - refonte propre

Date: 2026-07-09

## Objectif

Remplacer les chemins source-backed historiques par un pipeline canonique, modulaire et traçable :

`question utilisateur -> LLM planner/orchestrateur -> outils RAG -> EvidenceBundle type -> LLM evidence judge -> writer -> source verifier mecanique -> repair cible -> reponse UI + source cards`

Le LLM reste le decideur semantique. Le code garde les responsabilites mecaniques : contrats, outillage, budgets, traces, presence des sources, non-duplication, citations, formats et boucles de reparation.

## Etat valide ce matin

Commandes executees :

```powershell
dotnet build client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false
dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal
git diff --check
```

Resultats :

- Build OK, 0 warning, 0 erreur.
- Tests `SourceBackedRagArchitectureTests` OK : 4/4.
- Tests `SourceBackedRagArchitectureTests` OK : 6/6.
- Tests `SourceBackedRagPipelineTests` OK : 6/6.
- Total cible `FullyQualifiedName~SourceBackedRag` : 12/12.
- `git diff --check` OK. Il reste uniquement des avertissements de normalisation CRLF/LF sur des fichiers deja modifies.

## Socle propre ajoute

Nouveau dossier actif :

- `client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/`

Responsabilites actuelles :

- `SourceBackedPipelineStep.cs` : etapes canoniques du pipeline.
- `SourceBackedTraceEvent.cs` : evenements de trace standardises par etape.
- `EvidenceItem.cs` : unite de preuve RAG, generique et sourcee.
- `EvidenceBundle.cs` : paquet canonique de preuves transmis entre les etapes.
- `EvidenceBundleBuilder.cs` : conversion generique `ToolResults -> EvidenceBundle`.
- `SourceBackedPipelineContracts.cs` : contrats du planner, retrieval, judge, writer et verifier.
- `SourceContractVerifier.cs` : verification mecanique des citations et sources.
- `SourceBackedRagPipeline.cs` : pipeline canonique minimal apres retrieval.
- `SourceBackedRagPrompts.cs` : prompts separes par etape LLM avec balises de trace.
- `SourceBackedRagJson.cs` : parsing JSON strict des decisions LLM.
- `SourceBackedPipelineResult.cs` : sortie typee du pipeline pour integration future.
- `ISourceBackedRagToolExecutor.cs` : abstraction d'execution des outils RAG pour eviter de coupler le pipeline au monolithe.
- `SourceBackedRouterPlanAdapter.cs` : conversion mecanique `RetrievalPlan -> RouterPlan` pour reutiliser les handlers outils existants.
- `ToolAgentOrchestrator.SourceBackedRag.cs` : facade courte vers l'execution d'outils existante, sans remplacer encore le flux utilisateur principal.
- `SourceBackedUiPayload.cs` : sortie UI typee contenant reponse, sources visibles et traces.
- `SourceBackedUiPayloadMapper.cs` : conversion des preuves citees/verifiees vers `ToolMemory.SourceRef` pour les source cards.

Garde-fou teste :

- Tous les fichiers `.cs` du nouveau dossier doivent rester sous 500 lignes.
- Le builder preserve la lignee RAG sans hardcoding metier.
- Le verifier rejette les citations vers des preuves absentes du bundle.
- Le pipeline appelle le LLM evidence judge avant le writer.
- Le pipeline complet appelle maintenant le LLM planner, puis un executor d'outils abstrait, puis le judge, puis le writer.
- L'executor reel est teste hors UI : un `RetrievalPlan` est converti en `RouterPlan`, passe par `ExecuteToolsAsync`, appelle `/rag/search` via un `ApiClient` stub et revient en `ToolResults`.
- Le payload UI est teste : seules les preuves citees, verifiees et issues du `EvidenceBundle` deviennent des sources visibles.
- Un premier point d'entree actif est branche dans `ToolAgentOrchestrator.RunAsync` avant l'ancien `documentaryProbe` : quand le router a prevu un outil RAG utilisateur, la reponse peut maintenant passer par `SourceBackedRagPipeline`.
- Ce branchement actif est teste hors UI : `RunAsync` consomme un router plan RAG, appelle ensuite les prompts `Planner`, `EvidenceJudge`, `Writer`, execute `/rag/search` via `ApiClient` stub, puis retourne une reponse source-backed avec source visible.
- Le repair LLM est appele seulement apres erreur mecanique du source verifier.
- Les prompts portent les balises `SAAIA_SOURCE_BACKED_STEP=Planner`, `SAAIA_SOURCE_BACKED_STEP=EvidenceJudge`, `SAAIA_SOURCE_BACKED_STEP=Writer` et `SAAIA_SOURCE_BACKED_STEP=Repair`.

## Zone OLD

Nouveau dossier :

- `client/SAAIA.Client.WinUI/ToolAgent/OLD/`

Regle projet ajoutee :

- Les fichiers `.cs` sous `ToolAgent/OLD/**` sont exclus de la compilation.
- Les fichiers y restent consultables comme reference pendant l'extraction.

But : pouvoir isoler des morceaux de l'ancien pipeline sans les supprimer brutalement et sans risquer de les garder actifs par accident.

## Dette structurelle constatee

Taille des fichiers principaux :

- `ToolAgentOrchestrator.State.cs` : environ 34 090 lignes.
- `ToolAgentOrchestrator.cs` : environ 17 468 lignes.
- `ToolAgentOrchestrator.ReplayAndExec.cs` : environ 2 720 lignes.
- `ToolAgentOrchestrator.Shortcuts.cs` : environ 1 999 lignes.
- `ToolAgentOrchestrator.LiveSupport.cs` : environ 1 609 lignes.
- `ToolAgentOrchestrator.TestHooks.cs` : environ 1 590 lignes.

Occurrences ciblees des anciens chemins source-backed/planning :

- `ToolAgentOrchestrator.cs` : 120 occurrences.
- `ToolAgentOrchestrator.State.cs` : 120 occurrences.
- `ToolAgentOrchestrator.TestHooks.cs` : 8 occurrences.
- `ToolAgentOrchestrator.RagTrace.cs` : 3 occurrences.

Marqueurs principaux :

- `ShouldGateStructuredSourceBackedPlanningCoverage`
- `ShouldRequireDeterministicStructuredPlanningAnswer`
- `ShouldAllowWriterForPartialSourceBackedPlanning`
- `EvaluateSourceBackedPlanningCoverage`
- `TryBuildSupportedStructuredPlanningAnswer`
- `TryFinalizeSourceBackedPlanningResponse`
- `TryRepairSourceBackedSynthesisAnswerWithWriterAsync`
- `BuildBroadEvidenceStillInsufficientAnswer`

Conclusion : l'ancien comportement n'est pas concentre dans un composant remplacable. Il est eparpille dans le flux principal, dans l'etat, dans les traces et dans les test hooks. Continuer a patcher ces conditions entretient l'ancien systeme au lieu de le remplacer.

## Probleme architectural exact

Le pipeline historique melange encore plusieurs responsabilites :

- interpretation de l'intention utilisateur ;
- heuristiques de couverture source-backed ;
- selection de candidats ;
- jugement de suffisance ;
- reconstruction deterministe de reponses structurees ;
- writer LLM ;
- verification finale ;
- fallback d'insuffisance ;
- logique de source cards UI.

Dans la cible, ces responsabilites doivent devenir des modules separes. Le code ne doit plus juger semantiquement qu'une preuve "repond" a la question. Cette decision doit etre portee par le LLM evidence judge, avec des contrats stricts et des preuves inspectables.

## Plan d'action de migration

1. Stabiliser le socle canonique.

- Garder `EvidenceBundle`, `EvidenceItem`, traces et verifier comme source de verite.
- Ajouter les champs manquants uniquement s'ils servent le flux generique, pas un cas metier.
- Verrouiller les invariants par tests d'architecture.

2. Extraire les fonctions pures reutilisables.

- Sortir la lecture generique des hits RAG hors de `ToolAgentOrchestrator.State.cs`.
- Remplacer progressivement `RagHitSummary` historique par `EvidenceItem` ou un adaptateur mince.
- Garder les helpers utiles seulement s'ils ne decident pas semantiquement de la pertinence.

3. Introduire un orchestrateur source-backed dedie.

Responsabilites prevues :

- intake de la question ;
- appel au planner LLM ;
- execution des recherches RAG demandees ;
- construction du `EvidenceBundle` ;
- appel au LLM evidence judge ;
- iteration si le judge demande une recherche supplementaire ;
- appel writer ;
- verification mecanique ;
- repair cible si contrat invalide ;
- sortie typed pour UI et source cards.

4. Isoler les anciens chemins concurrents.

- Deplacer dans `ToolAgent/OLD` les blocs devenus non actifs, en `.cs` exclu ou en fichiers reference.
- Remplacer les appels actifs par des facades courtes vers le nouveau pipeline.
- Supprimer les fallbacks qui reconstruisent des reponses source-backed sans passer par le judge LLM.

5. Remettre les tests au bon niveau.

- Tests unitaires generiques sur `EvidenceBundle`.
- Tests de contrat sur citations/sources/source cards.
- Tests d'integration avec donnees non cuisine.
- Tests regression cuisine uniquement comme exemples, pas comme logique codee en dur.
- Test UI reel final depuis l'application client quand le pipeline canonique est branche.

## Prochaine coupe technique recommandee

Ne pas commencer par deplacer 34 000 lignes. Le service `SourceBackedRagPipeline` existe maintenant pour le flux planner + retrieval executor reel + EvidenceBundle + judge + writer + verifier + repair, et un premier branchement actif est en place. La prochaine coupe propre doit etre :

1. etendre le branchement actif aux cas encore interceptes par les anciens shortcuts/probes source-backed ;
2. isoler en `OLD` les fallbacks source-backed historiques devenus inactifs ;
3. executer une integration RAG plus large avec plusieurs categories non cuisine ;
4. preparer ensuite le vrai test UI client.

Cette approche evite deux risques :

- casser brutalement le client avant d'avoir une alternative testee ;
- garder l'ancien pipeline actif sous une nouvelle couche de facade.

## Statut

La refonte n'est pas terminee.

Ce qui est solide maintenant :

- le goal architectural est clair ;
- le socle canonique compile ;
- les premiers garde-fous sont testes ;
- le dossier `OLD` est non compilable ;
- la dette et les anciens points d'accroche sont identifies.

Ce qu'il reste a faire :

- extraire les adaptateurs RAG utiles hors du monolithe ;
- creer le pipeline source-backed dedie ;
- brancher le LLM judge et writer autour de contrats explicites ;
- neutraliser les anciens fallbacks concurrents ;
- verifier par tests d'integration generiques ;
- finir par un vrai test UI client.

## Mise a jour - 2026-07-09 - branchement fallback standalone vers pipeline canonique

Nouvelle coupe realisee :

- `ToolAgentOrchestrator.cs` appelle maintenant `TryHandleStandaloneFallbackSourceBackedRagPipelineAsync` avant `TryHandleStandaloneTopicRagAsync`.
- Les cas documentaires courts ou fallback local qui auraient pu partir dans l'ancien rendu `standalone_topic_rag` donnent d'abord la main au pipeline canonique.
- `ToolAgentOrchestrator.SourceBackedRag.cs` factorise l'execution commune dans `RunSourceBackedRagPipelineAsync`.
- Les traces portent maintenant une balise `entry` :
  - `router_plan` pour les plans RAG explicites du router ;
  - `standalone_fallback` pour les fallbacks documentaires repris avant l'ancien chemin.
- Le pipeline force l'intention canonique `rag.answer` pour le fallback standalone, sans ajouter de logique metier specifique.

Validation ajoutee :

- `SourceBackedRagPipelineTests.Orchestrator_run_routes_standalone_fallback_topic_through_source_backed_pipeline_before_legacy_path`
  - simule un router LLM qui renvoie du non-JSON ;
  - verifie que le fallback local sur `ATEX` passe par :
    - planner LLM source-backed ;
    - `/rag/search` ;
    - `EvidenceBundle` ;
    - evidence judge LLM ;
    - writer LLM ;
    - source cards ;
  - verifie que la reponse finale n'est pas le vieux rendu deterministe `I found ...`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 13/13.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:minimal`
  - OK, 38/38.
- `git diff --check`
  - OK. Seulement les avertissements de normalisation CRLF/LF deja presents dans le worktree.

Statut apres cette coupe :

- Le pipeline canonique n'est plus seulement branche sur les plans RAG explicites.
- Il reprend aussi la main sur une classe de fallbacks locaux qui etait historiquement traitee par l'ancien chemin standalone.
- L'ancien standalone reste encore comme filet si le pipeline canonique ne produit pas de reponse source-verifiee ; il n'est donc pas encore totalement isolable en `OLD`.

Prochaine coupe recommandee :

1. Auditer les autres chemins actifs avant/apres le pipeline canonique :
   - `TryHandleDocumentaryProbeAsync`
   - `TryHandleSourcePolicyShortcutAsync`
   - `TryHandleDocumentVersionTraceabilityShortcutAsync`
   - `TryHandleExactItemPreRouterShortcutAsync`
2. Decider lesquels sont de simples outils/shortcuts mecaniques acceptables, et lesquels reconstruisent encore une reponse semantique hors pipeline.
3. Migrer les chemins semantiques vers le pipeline `SourceBackedRag`, puis deplacer les blocs devenus inactifs dans `ToolAgent/OLD`.
4. Continuer a reduire le monolithe sans recreer un nouveau fichier geant dans les tests ou dans le partial source-backed.

## Mise a jour - 2026-07-09 - exact item retire du pre-router

Nouvelle coupe realisee :

- L'ancien appel `TryHandleExactItemPreRouterShortcutAsync` a ete retire de la zone avant router.
- La methode a ete renommee `TryHandleExactItemLegacyShortcutAsync` pour refleter son nouveau role.
- Le router LLM et le pipeline canonique source-backed passent maintenant avant ce chemin exact-item.
- Le fallback exact-item legacy reste disponible seulement apres :
  - le router ;
  - les clarifications documentaires ;
  - `TryHandleSourceBackedRagPipelineAsync`.
- La trace active n'est plus `exact_item_prerouter_shortcut`, mais `exact_item_legacy_fallback`.

Pourquoi c'est important :

- Avant cette coupe, une question source-backed exacte pouvait recevoir une reponse reconstruite par code avant meme que le router et le planner LLM du pipeline canonique aient travaille.
- Maintenant, le chemin normal est plus conforme a l'architecture cible : interpretation LLM, retrieval planner LLM, EvidenceBundle, evidence judge LLM, writer LLM, verification mecanique.
- Le fallback legacy garde une valeur de securite pendant la migration, mais il ne gouverne plus le flux principal.

Robustesse ajoutee :

- `RunSourceBackedRagPipelineAsync` trace maintenant `source_backed_pipeline.active.failed` et rend la main aux fallbacks si le pipeline LLM echoue par exception non liee a l'annulation.
- Les `OperationCanceledException` continuent a remonter normalement.

Tests et structure :

- Ajout de `SourceBackedRagOrchestratorRoutingTests.cs`.
- Ajout de `SourceBackedRagTestDoubles.cs` pour sortir les doubles de test du fichier principal.
- `SourceBackedRagPipelineTests.cs` est redescendu a 447 lignes.
- `ToolAgentOrchestrator.SourceBackedRag.cs` reste a 197 lignes.

Validation ajoutee :

- `SourceBackedRagOrchestratorRoutingTests.Exact_item_source_backed_requests_reach_router_and_canonical_pipeline_before_legacy_fallback`
  - simule une demande exacte non cuisine sur `PumpManual.pdf` ;
  - prouve que le router LLM est appele ;
  - prouve que le pipeline canonique appelle planner, RAG, EvidenceBundle, judge et writer ;
  - prouve que les sources UI viennent du resultat verifie.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 14/14.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:minimal`
  - OK, 94/94.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.

Prochaine coupe recommandee :

1. Migrer `TryHandleDocumentVersionTraceabilityShortcutAsync`, qui est encore un shortcut source-backed avant router.
2. Garder `TryHandleSourcePolicyShortcutAsync` sous audit separe : une partie est un garde-fou mecanique acceptable, une partie produit encore une reponse extractive.
3. Auditer `TryHandleDocumentaryProbeAsync` apres pipeline : il reste surtout utile comme fallback, mais contient encore des writers/finalizers historiques.

## Mise a jour - 2026-07-09 - version traceability retire du pre-router

Nouvelle coupe realisee :

- L'ancien shortcut `TryHandleDocumentVersionTraceabilityShortcutAsync` a ete retire de la zone avant router.
- La methode a ete renommee `TryHandleDocumentVersionTraceabilityLegacyShortcutAsync`.
- Le chemin normal pour les demandes de version/corrigendum/ancienne version passe maintenant par le router LLM puis par le pipeline source-backed canonique.
- Le fallback legacy reste disponible apres le pipeline, avant le fallback exact-item.
- La trace active n'est plus `document_version_traceability_shortcut`, mais `document_version_traceability_legacy_fallback`.

Pourquoi c'est important :

- Ce shortcut construisait une recherche et une reponse de traçabilite par code avant l'orchestration LLM.
- Le cas version/corrigendum est semantique : il faut que le LLM judge decide quelles preuves expliquent correctement la relation entre version principale, corrigendum, ancienne version ou version actuelle.
- Le code doit seulement assurer l'execution, les contrats, les sources visibles et la verification mecanique.

Validation ajoutee :

- `SourceBackedRagOrchestratorRoutingTests.Version_traceability_requests_reach_router_and_canonical_pipeline_before_legacy_fallback`
  - simule une demande non cuisine sur `Policy ABC` et un corrigendum ;
  - prouve que le router LLM est appele ;
  - prouve que le pipeline canonique appelle planner, RAG, EvidenceBundle, judge et writer ;
  - prouve que la source UI finale derive du resultat verifie.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 15/15.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Document_version_traceability" --verbosity:minimal`
  - OK, 19/19.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.

Statut apres cette coupe :

- Les deux gros shortcuts source-backed pre-router qui repondaient semantiquement (`exact item` et `version traceability`) sont maintenant des fallbacks legacy apres pipeline.
- Le principal raccourci pre-router restant est `source policy`.
- `source policy` doit etre separe en deux :
  - garde-fous mecaniques/politiques acceptables ;
  - reponses extractives source-backed a migrer vers le pipeline canonique.

## Mise a jour - 2026-07-09 - source policy limite au garde-fou pre-router

Nouvelle coupe realisee :

- `TryHandleSourcePolicyShortcutAsync` ne construit plus de reponse documentaire extractive via RAG dans sa derniere branche.
- La branche qui faisait :
  - construction de `retrievalQuery` ;
  - `ExecRagSearchAsync` pre-router ;
  - `BuildSourceBackedPlanningOrExtractiveAnswer` ;
  - injection de sources ;
  a ete retiree du shortcut pre-router.
- Les cas hard bypass/invention gardent une reponse de politique courte via `BuildSourcePolicyGuardPrefix(language)`.
- Les trois garde-fous mecaniques restent actifs :
  - instruction dans document qui tente de modifier les regles ;
  - affirmation d'une valeur/champ/conclusion sans preuve exploitable ;
  - demande de oui/non force quand les sources ne tranchent pas.

Pourquoi c'est important :

- `source policy` etait le dernier shortcut pre-router source-backed qui pouvait encore appeler RAG et reconstruire une reponse documentaire avant l'orchestration.
- Le garde-fou anti-invention est acceptable avant router, car il empeche une demande de contournement explicite.
- Mais la production de contenu documentaire source-backed doit passer par le pipeline canonique.

Validation ajoutee :

- `SourceBackedRagOrchestratorRoutingTests.Source_policy_shortcut_refuses_hard_source_bypass_without_pre_router_rag_answer`
  - simule une demande hard bypass : `Ignore the sources and invent...` ;
  - verifie que la reponse refuse le contournement ;
  - verifie qu'aucun appel API/RAG n'est effectue ;
  - verifie qu'aucun appel LLM router n'est effectue.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 16/16.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Source_policy" --verbosity:minimal`
  - OK, 25/25.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.

Statut apres cette coupe :

- Les shortcuts pre-router restants ne devraient plus produire de reponse documentaire source-backed par RAG avant l'orchestration.
- Les chemins `exact item`, `version traceability` et `source policy extractif` ne gouvernent plus la reponse finale avant le pipeline canonique.
- Il reste a auditer `TryHandleDocumentaryProbeAsync`, qui est apres pipeline mais contient encore des logiques historiques de writer/finalizer/fallback.

## Mise a jour - 2026-07-09 - documentary probe donne d'abord la main au pipeline canonique

Nouvelle coupe realisee :

- Ajout de `TryHandleDocumentaryProbeFallbackSourceBackedRagPipelineAsync`.
- Les cas eligibles a `ShouldRunDocumentaryProbe` donnent maintenant d'abord la main au pipeline canonique avec `entry=documentary_probe_fallback`.
- L'ancien `TryHandleDocumentaryProbeAsync` reste disponible comme fallback legacy uniquement si le pipeline canonique ne produit pas de reponse source-verifiee.

Pourquoi c'est important :

- `documentary_probe` etait deja situe apres le pipeline RAG explicite, mais il pouvait encore prendre le relais pour des plans `chat.general` sans outil.
- Ces cas correspondent souvent a des questions documentaires vagues ou courtes.
- La cible reste que le LLM planner decide des recherches, puis que l'EvidenceBundle, le judge, le writer et le verifier source structurent la reponse.

Validation ajoutee :

- `SourceBackedRagOrchestratorRoutingTests.Documentary_probe_fallback_reaches_canonical_pipeline_before_legacy_probe`
  - simule un router LLM qui choisit `chat.general` sans outil ;
  - utilise une question documentaire courte `Audit cadence?` ;
  - prouve que le pipeline canonique appelle planner, RAG, EvidenceBundle, judge et writer ;
  - prouve que le vieux probe n'est plus le premier chemin de reponse.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 17/17.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:minimal`
  - OK, 38/38.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.

Statut apres cette coupe :

- Les anciens chemins concurrents majeurs ne sont plus prioritaires sur le pipeline canonique :
  - standalone fallback ;
  - exact item ;
  - version traceability ;
  - source policy extractif ;
  - documentary probe.
- Les anciens blocs restent encore dans le code comme fallbacks legacy consultables et securisants pendant la migration.
- La prochaine etape n'est plus d'ajouter un nouveau patch de routage, mais de consolider :
  - audit des fallbacks legacy restants ;
  - extraction des parties utiles hors du monolithe ;
  - integration RAG generique plus large ;
  - test UI reel client.

## Mise a jour - 2026-07-09 - iteration LLM judge -> follow-up RAG

Nouvelle coupe realisee :

- `SourceBackedRagPipeline` execute maintenant les `followUpRequests` produits par le LLM evidence judge.
- Le code agit comme controleur mecanique :
  - budget maximum de 3 tours evidence judge ;
  - execution uniquement des requetes RAG sures avec query non vide (`rag.search`, `rag.multi_search`) ;
  - accumulation des `ToolResults` ;
  - reconstruction d'un `EvidenceBundle` unique depuis tout l'inventaire cumule ;
  - traces explicites.
- Ajout du step `IterationController` dans `SourceBackedPipelineStep`.
- Ajout des traces :
  - `evidence_iteration.follow_up_requested`
  - `evidence_iteration.stopped`
- Le writer n'est appele qu'apres une decision `answer` du LLM evidence judge avec au moins un `evidenceId` selectionne.

Pourquoi c'est important :

- Le contrat contenait deja `followUpRequests`, mais le pipeline ne les executait pas.
- Cela limitait le LLM : il pouvait identifier une lacune, mais le code ne lui donnait pas la possibilite d'aller chercher la preuve manquante.
- La nouvelle boucle rapproche le pipeline de l'architecture cible : le LLM decide semantiquement s'il faut chercher encore ; le code execute, trace et limite les budgets.

Validation ajoutee :

- `SourceBackedRagIterationTests.Run_executes_evidence_judge_follow_up_requests_before_writing`
  - premiere recherche : preuve de contexte insuffisante ;
  - premier evidence judge : `need_more_evidence` avec follow-up RAG ;
  - execution du follow-up ;
  - reconstruction de l'EvidenceBundle avec les deux preuves ;
  - second evidence judge : `answer` avec `E2` ;
  - writer final cite `E2` ;
  - source verifiee issue du second resultat.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 18/18.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagIterationTests" --verbosity:normal`
  - OK, 1/1.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier :
  - aucun `cuisine|recette|repas|meal|recipe|tartiflette` dans `SourceBackedRag` ni dans `SourceBackedRagIterationTests.cs`.

Statut apres cette coupe :

- Le pipeline canonique n'est plus seulement lineaire.
- Le LLM evidence judge peut maintenant piloter une recherche complementaire et faire evoluer l'inventaire de preuves.
- Le code reste dans son role : contrats, execution d'outils, budget, traces, EvidenceBundle, verifier, repair.
- La prochaine consolidation utile est l'integration client/orchestrator sur un cas multi-recherche reel, puis une validation UI reelle.

## Mise a jour - 2026-07-09 - follow-up judge valide via executor ToolAgent

Nouvelle coupe realisee :

- Ajout d'une validation orchestrateur qui prouve que les `followUpRequests` du LLM evidence judge traversent aussi l'executor reel du `ToolAgentOrchestrator`.
- Le test simule deux appels `/rag/search` :
  - premiere recherche : preuve de contexte insuffisante ;
  - follow-up demande par le judge : preuve exacte de cadence.
- `ToolMemory.LastRagQueries` contient maintenant la requete initiale et la requete follow-up.
- `ToolMemory.LastRagTraceEvents` contient `IterationController`, ce qui permet de diagnostiquer le chemin exact de la reponse depuis l'UI/logs.

Validation ajoutee :

- `SourceBackedRagOrchestratorRoutingTests.Orchestrator_run_executes_evidence_judge_follow_up_requests_through_tool_executor`
  - router LLM -> plan RAG ;
  - planner LLM -> premiere recherche ;
  - evidence judge -> `need_more_evidence` + follow-up ;
  - executor ToolAgent -> deuxieme `/rag/search` ;
  - EvidenceBundle cumule ;
  - second judge -> `answer` ;
  - writer -> citation `E2` ;
  - source cards depuis la preuve follow-up.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 19/19.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Orchestrator_run_executes_evidence_judge_follow_up_requests" --verbosity:normal`
  - OK, 1/1.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier :
  - aucun `cuisine|recette|repas|meal|recipe|tartiflette` dans `SourceBackedRag`, `SourceBackedRagIterationTests.cs` et `SourceBackedRagOrchestratorRoutingTests.cs`.

Statut apres cette coupe :

- La boucle LLM judge -> follow-up retrieval est validee au niveau pipeline pur et au niveau orchestrateur client.
- Le pipeline canonique commence a couvrir le comportement attendu pour des questions larges ou incompletes : chercher, juger, chercher encore si le LLM le demande, puis ecrire avec sources verifiees.
- Prochain verrou : validation integration plus proche du runtime/UI, avec donnees reelles ou backend disponible, puis test UI client.

## Mise a jour - 2026-07-09 - source contract verifier renforce

Nouvelle coupe realisee :

- `SourceContractVerifier` separe maintenant :
  - les citations visibles dans le texte (`[E#]`) ;
  - les IDs declares dans `citedEvidenceIds`.
- Si le writer declare `E1` dans `citedEvidenceIds` mais n'affiche pas `[E1]` dans la reponse visible, le verifier retourne `missing_inline_citation`.
- Les preuves citees doivent maintenant avoir une source UI exploitable :
  - document visible (`docPath` ou `docName`) ;
  - page visible valide.
- Si ces metadonnees manquent, le verifier retourne :
  - `missing_visible_source` ;
  - `missing_visible_page`.

Pourquoi c'est important :

- Avant cette coupe, une source pouvait etre presente dans le payload UI mais absente du texte utilisateur.
- Cela cassait la tracabilite phrase -> preuve, meme si la source card existait.
- Le renforcement reste mecanique : le code ne juge pas si la preuve repond a la question, il verifie seulement que les citations et metadonnees visibles existent.

Validation ajoutee :

- `Source_contract_verifier_requires_declared_ids_to_be_visible_inline_citations`
- `Source_contract_verifier_requires_cited_evidence_to_have_visible_source_metadata`

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 21/21.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Source_contract_verifier" --verbosity:normal`
  - OK, 3/3.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier :
  - la seule occurrence `cuisine` dans les fichiers source-backed cibles est une assertion `Assert.DoesNotContain("cuisine", ...)`.

Statut apres cette coupe :

- Le writer ne peut plus masquer une source uniquement dans `citedEvidenceIds`.
- Les source cards source-backed ne peuvent plus etre construites depuis une preuve citee sans document/page visible.
- Le verifier reste dans son role mecanique et ne prend pas de decision semantique sur la pertinence de la preuve.

## Mise a jour - 2026-07-09 - plus de reprise legacy apres rejet du evidence judge

Nouvelle coupe realisee :

- Quand le pipeline source-backed canonique est lance, il ne rend plus `handled=false` apres un resultat non verifie.
- Si le LLM evidence judge retourne `need_more_evidence` sans follow-up executable, le tour se termine maintenant par une reponse terminale canonique.
- Si le writer ou le repair ne passe pas le `SourceContractVerifier`, le tour se termine aussi dans le pipeline canonique, sans source card non verifiee.
- Si le pipeline echoue techniquement, il retourne une reponse terminale explicite au lieu de laisser un ancien fallback produire une reponse non verifiee.
- Extraction structurelle :
  - `SourceBackedTerminalAnswer.cs` construit les sorties terminales generiques.
  - `ToolAgentOrchestrator.SourceBackedRagTerminal.cs` contient la sortie terminale cote orchestrateur.
  - `ToolAgentOrchestrator.SourceBackedRagToolExecutor.cs` contient l'executor d'outils source-backed.
  - `ToolAgentOrchestrator.SourceBackedRag.cs` reste court et centre sur le branchement actif.

Pourquoi c'est important :

- Avant cette coupe, un resultat `need_more_evidence` ou non verifie pouvait redonner la main aux anciens chemins source-backed.
- Cela pouvait contredire le LLM judge : le judge disait "preuve insuffisante", puis du code historique pouvait reconstruire une reponse.
- La decision semantique reste maintenant dans le LLM evidence judge ; le code ne fait que convertir cette decision en sortie terminale sure.

Validation ajoutee :

- `SourceBackedRagPipelineTests.Orchestrator_run_stops_in_canonical_pipeline_when_judge_rejects_evidence`
  - router LLM -> plan RAG ;
  - planner LLM -> recherche RAG ;
  - evidence judge -> `need_more_evidence` sans follow-up ;
  - aucun writer appele ;
  - aucune source card produite ;
  - `LastAnswerSource` commence par `source_backed_pipeline_terminal:` ;
  - l'ancien pipeline ne reprend pas la main.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
  - Une premiere tentative avec timeout 120s a expire sans sortie utile ; relancee avec timeout plus long, OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 22/22.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Verification structure :
  - `SourceBackedRagPipeline.cs` : 350 lignes.
  - `ToolAgentOrchestrator.SourceBackedRag.cs` : 238 lignes.
  - nouveaux fichiers terminaux/executor : moins de 100 lignes chacun.
- Recherche ciblee hardcoding metier :
  - aucune logique metier categorie dans les nouveaux fichiers.
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Statut apres cette coupe :

- Le pipeline canonique est plus ferme : quand il est responsable d'un tour, il ne laisse plus l'ancien pipeline prendre une decision de remplacement apres rejet ou echec de verification.
- Il reste encore beaucoup de dette legacy dans `ToolAgentOrchestrator.cs` et `ToolAgentOrchestrator.State.cs`, mais ce verrou reduit un risque majeur de double decision.
- Prochain verrou recommande : neutraliser les branches post-tools `pre_writer_source_backed` et finalizers historiques pour les tours RAG deja couverts par le pipeline canonique.

## Mise a jour - 2026-07-09 - intention generale avec outils RAG routee au pipeline canonique

Nouvelle coupe realisee :

- Suppression d'une restriction dans `ShouldUseSourceBackedRagPipeline` qui excluait les plans dont l'intention etait `chat.general`.
- La regle devient plus saine :
  - sans outil RAG, `chat.general` reste hors pipeline source-backed ;
  - avec `rag.search` ou `rag.multi_search`, le pipeline source-backed prend la main.

Pourquoi c'est important :

- Le label d'intention du router ne doit pas bloquer le pipeline quand le router a deja demande une recherche RAG.
- Ce n'est pas au code de dire "cette intention generale ne merite pas le pipeline" si les outils prevus montrent qu'il faut consulter les sources.
- Le LLM evidence judge reste le decideur semantique apres retrieval.

Validation ajoutee :

- `SourceBackedRagPipelineTests.Orchestrator_run_routes_general_intent_with_rag_tools_through_source_backed_pipeline`
  - router retourne `intent: chat.general` avec `rag.search` ;
  - le tour passe quand meme par `source_backed_pipeline:router_plan` ;
  - planner, evidence judge et writer source-backed sont appeles ;
  - la source visible provient de l'EvidenceBundle verifie.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 23/23.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Verification structure :
  - `ToolAgentOrchestrator.SourceBackedRag.cs` : 237 lignes.
  - `ToolAgentOrchestrator.SourceBackedRagTerminal.cs` : 93 lignes.
  - `ToolAgentOrchestrator.SourceBackedRagToolExecutor.cs` : 29 lignes.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Statut apres cette coupe :

- Le pipeline canonique couvre maintenant aussi le cas ou le router garde un libelle general mais demande explicitement des outils RAG.
- Cela reduit un autre risque de reprise par les anciens chemins apres une decision de routing imparfaite.
- Prochaine priorite maintenue : auditer et neutraliser les branches post-tools historiques qui fabriquent encore des reponses source-backed hors du pipeline canonique.

## Mise a jour - 2026-07-09 - terminal standalone sans reprise legacy

Nouvelle coupe realisee :

- Ajout d'un verrou de regression sur le fallback standalone.
- Scenario teste :
  - le router tombe en fallback local sur un sujet court ;
  - le pipeline source-backed standalone est lance ;
  - le planner canonique ne produit aucune requete executable ;
  - le pipeline retourne une insuffisance terminale ;
  - l'ancien `standalone_topic_rag` ne reprend pas la main ;
  - aucun appel API RAG historique n'est execute.

Pourquoi c'est important :

- Le vieux `standalone_topic_rag` contient encore beaucoup de logique deterministe : couverture, drafts, writer historique, finalizers.
- Meme si ce code existe encore, il ne doit pas reprendre le controle apres une terminaison canonique du pipeline source-backed.
- Ce test verrouille le comportement attendu avant de supprimer ou isoler plus agressivement les anciennes branches.

Validation ajoutee :

- `SourceBackedRagPipelineTests.Orchestrator_run_terminal_standalone_pipeline_does_not_fall_through_to_legacy_standalone_rag`
  - reponse terminale canonique d'insuffisance ;
  - `sourcesPayload == null` ;
  - `apiWasCalled == false` ;
  - `LastAnswerSource` commence par `source_backed_pipeline_terminal:standalone_fallback:`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 24/24.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Statut apres cette coupe :

- Les chemins canonicalises couvrent maintenant le succes, l'insuffisance judge, l'echec de verification, l'intention generale avec RAG, et le fallback standalone terminal.
- L'ancien code standalone reste physiquement present, mais il est davantage encadre par des verrous qui empechent une reprise silencieuse apres decision canonique.
- La prochaine etape structurelle reste l'extraction/neutralisation des branches historiques post-tools et des finalizers source-backed eparpilles.

## Mise a jour - 2026-07-09 - ancien standalone retire du flux principal

Nouvelle coupe realisee :

- Suppression de l'appel actif a `TryHandleStandaloneTopicRagAsync` dans `ToolAgentOrchestrator.RunAsync`.
- L'ancien bloc reste consultable dans le fichier pour l'instant, mais il n'est plus appele depuis le flux principal.
- Ajout d'un test d'architecture qui verrouille cette situation :
  - une seule occurrence de `TryHandleStandaloneTopicRagAsync(` dans `ToolAgentOrchestrator.cs` ;
  - cette occurrence est la definition legacy, pas un appel.

Pourquoi c'est important :

- Le nouveau fallback `TryHandleStandaloneFallbackSourceBackedRagPipelineAsync` utilise la meme condition d'activation que l'ancien standalone.
- Comme le pipeline canonique retourne maintenant une sortie terminale en cas d'insuffisance ou d'echec, l'ancien standalone devenait un filet historique inutile.
- Le laisser appele dans le flux principal gardait une ambiguite structurelle : deux chemins pouvaient sembler responsables du meme type de tour.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Legacy_standalone_topic_rag_is_no_longer_called_from_main_turn_flow`

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 25/25.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Statut apres cette coupe :

- L'ancien standalone source-backed n'est plus un fallback actif du flux principal.
- Le code legacy n'est pas encore physiquement extrait dans `OLD`, mais il est maintenant desactive au niveau appel.
- Prochaine coupe recommandee : faire le meme type d'audit sur les anciens chemins `documentary_probe`, `exact_item_legacy_fallback`, `document_version_traceability_legacy_fallback` et sur les branches post-tools encore capables de reconstruire une reponse source-backed hors `EvidenceBundle`.

## Mise a jour - 2026-07-09 - ancien documentary_probe retire du flux principal

Nouvelle coupe realisee :

- Suppression de l'appel actif a `TryHandleDocumentaryProbeAsync` dans `ToolAgentOrchestrator.RunAsync`.
- Le fallback documentaire canonique `TryHandleDocumentaryProbeFallbackSourceBackedRagPipelineAsync` est deja execute avant l'ancien probe.
- Les deux chemins s'appuient sur la meme porte d'entree `ShouldRunDocumentaryProbe`.
- L'ancien bloc reste consultable dans le fichier pour l'instant, mais il n'est plus appele depuis le flux principal.

Pourquoi c'est important :

- `TryHandleDocumentaryProbeAsync` contenait encore plusieurs decisions deterministes : retrieval direct, broad exploration, writer historique, source policy guard, clarification probe.
- Le laisser actif apres le pipeline canonique maintenait une deuxieme voie capable de produire une reponse source-backed sans passer par `EvidenceBundle -> EvidenceJudge -> Writer -> SourceVerifier`.
- Le retirer du flux principal reduit la surface de double decision.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Legacy_documentary_probe_is_no_longer_called_from_main_turn_flow`
  - une seule occurrence de `TryHandleDocumentaryProbeAsync(` dans `ToolAgentOrchestrator.cs` ;
  - cette occurrence est la definition legacy, pas un appel actif.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 26/26.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Statut apres cette coupe :

- Les anciens chemins standalone et documentary probe ne sont plus appeles depuis le flux principal.
- Le pipeline canonique prend maintenant la responsabilite de ces fallback RAG documentaires.
- Prochaine coupe recommandee : traiter les fallbacks `exact_item_legacy_fallback` et `document_version_traceability_legacy_fallback`, puis les branches post-tools/finalizers historiques.

## Mise a jour - 2026-07-09 - exact item et version traceability remplaces par des fallbacks canoniques

Nouvelle coupe realisee :

- Ajout de `ToolAgentOrchestrator.SourceBackedRagFallbacks.cs`.
- Ce fichier contient deux entrees courtes vers le pipeline canonique :
  - `document_version_traceability_fallback` ;
  - `exact_item_fallback`.
- Suppression des appels actifs a :
  - `TryHandleDocumentVersionTraceabilityLegacyShortcutAsync` ;
  - `TryHandleExactItemLegacyShortcutAsync`.
- Les anciens blocs restent encore consultables dans `ToolAgentOrchestrator.cs`, mais ils ne sont plus appeles depuis le flux principal.
- Elargissement generique de la detection version/traceability en anglais :
  - `mention`, `cite`, `citation`, `confuse`, `confusion`, etc.
  - But : router les demandes de citation/corrigendum/version vers le fallback version canonique plutot que vers exact-item.

Pourquoi c'est important :

- Les anciens shortcuts exact/version faisaient du retrieval direct et construisaient ensuite des reponses avec logique deterministe.
- Le remplacement par des entrees canoniques garde seulement la detection de route, puis laisse le LLM planner choisir les recherches, le LLM evidence judge juger les preuves, et le verifier mecanique controler les sources.
- Cela reduit encore la surface ou le code pouvait etre decideur final hors `EvidenceBundle`.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Legacy_exact_item_and_version_shortcuts_are_no_longer_called_from_main_turn_flow`
  - une seule occurrence par ancien shortcut dans `ToolAgentOrchestrator.cs` ;
  - cette occurrence est la definition legacy, pas un appel actif.
- `SourceBackedRagOrchestratorRoutingTests.Exact_item_fallback_without_router_rag_uses_canonical_pipeline`
  - router sans outil RAG ;
  - fallback exact-item canonique ;
  - planner/evidence judge/writer/verifier ;
  - source visible depuis `EvidenceBundle`.
- `SourceBackedRagOrchestratorRoutingTests.Version_traceability_fallback_without_router_rag_uses_canonical_pipeline`
  - router sans outil RAG ;
  - fallback version traceability canonique ;
  - planner/evidence judge/writer/verifier ;
  - source visible depuis `EvidenceBundle`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 29/29.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Verification structure :
  - `ToolAgentOrchestrator.SourceBackedRagFallbacks.cs` : 79 lignes.
  - `ToolAgentOrchestrator.SourceBackedRag.cs` : 237 lignes.
  - tous les fichiers du dossier `SourceBackedRag` restent sous 500 lignes.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Statut apres cette coupe :

- Les anciens chemins standalone, documentary probe, exact-item et version traceability ne sont plus appeles depuis le flux principal.
- Le pipeline canonique prend maintenant la responsabilite de ces fallbacks RAG.
- La dette restante principale se concentre dans les branches post-tools/finalizers historiques encore presentes dans le monolithe et dans les gros fichiers non encore extraits physiquement vers `OLD`.

## Mise a jour - 2026-07-09 - garde-fou post-tools RAG avant les anciens finalizers

Nouvelle coupe realisee :

- Ajout d'un garde-fou `TryHandlePostToolRagResultsSourceBackedRagPipelineAsync`.
- Positionnement dans `ToolAgentOrchestrator.RunAsync` juste apres l'execution d'outils et les echecs d'outils deterministes, avant :
  - `TryExpandSourceBackedEvidenceRetrievalAsync` ;
  - `TryBuildNoRagEvidenceAnswerForEmptySearch` ;
  - `TryBuildAmbiguousBareDocumentaryFragmentAnswer` ;
  - `pre_writer_source_backed` ;
  - les anciens finalizers/writer guards source-backed.
- Si des resultats `rag.search` ou `rag.multi_search` arrivent encore jusque-la, ils sont repris par :
  - `EvidenceBundleBuilder` ;
  - LLM `EvidenceJudge` ;
  - LLM `Writer` ;
  - `SourceContractVerifier` ;
  - repair si necessaire ;
  - sortie UI/source cards via le mapping canonique.
- Extraction de la completion "verified source-backed result" dans `CompleteVerifiedSourceBackedPipelineAsync`, reutilisee par le pipeline normal et par le garde-fou post-tools.

Pourquoi c'est important :

- Les anciens blocs post-tools contiennent encore beaucoup de logique deterministe source-backed.
- Meme si les principaux chemins RAG passent maintenant avant eux, ce garde-fou empeche qu'un resultat RAG residuel atteigne directement les anciens finalizers.
- Le code reste dans son role mecanique : detecter la presence d'un resultat RAG et le router vers le pipeline canonique, sans juger si la source repond a la question.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Post_tool_rag_results_reach_canonical_pipeline_before_legacy_source_backed_finalizers`
  - verifie que le garde-fou post-tools est place avant l'expansion source-backed historique ;
  - verifie qu'il est place avant `pre_writer_source_backed`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - premiere tentative : erreur de namespace manquant pour `DeterministicAgentText` dans les nouveaux fichiers ;
  - correction par ajout de `using SAAIA.Client.WinUI.Localization;` ;
  - relance OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 30/30.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Verification structure :
  - `ToolAgentOrchestrator.SourceBackedRag.cs` : 210 lignes.
  - `ToolAgentOrchestrator.SourceBackedRagFallbacks.cs` : 153 lignes.
  - `ToolAgentOrchestrator.SourceBackedRagTerminal.cs` : 148 lignes.
  - `ToolAgentOrchestrator.SourceBackedRagToolExecutor.cs` : 29 lignes.
  - tous les fichiers du dossier `SourceBackedRag` restent sous 500 lignes.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Statut apres cette coupe :

- Les chemins RAG principaux passent par le pipeline canonique avant les anciens blocs.
- Si un resultat RAG residuel arrive encore au niveau post-tools, il est repris par le pipeline canonique avant les finalizers historiques.
- La prochaine dette structurelle est moins comportementale et plus physique : extraire ou supprimer les blocs legacy devenus non appeles, puis poursuivre vers la validation runtime/live et le test UI client.

## Mise a jour - 2026-07-09 - handlers RAG legacy sortis de la compilation active

Nouvelle coupe realisee :

- Les gros handlers RAG historiques qui etaient deja non appeles sont maintenant places derriere un symbole de compilation legacy non defini par defaut :
  - `TryHandleDocumentaryProbeAsync` ;
  - `TryHandleDocumentVersionTraceabilityLegacyShortcutAsync` ;
  - `TryHandleExactItemLegacyShortcutAsync` ;
  - `TryHandleStandaloneTopicRagAsync`.
- Le symbole utilise est `SAAIA_LEGACY_RAG_FALLBACKS`.
- Le contenu reste consultable dans `ToolAgentOrchestrator.cs`, mais il ne fait plus partie du binaire tant que ce symbole n'est pas explicitement reactive.
- Les helpers encore utilises par le pipeline canonique ou par les tests de garde-fou ne sont pas coupes brutalement :
  - `ShouldRunDocumentaryProbe` reste actif pour declencher le fallback canonique documentary probe ;
  - les helpers de couverture/source-backed encore appeles par le garde-fou post-tools restent actifs ;
  - `ShouldSkipExactItemPreRouterShortcut` et `ShouldTryPreciseMultiSearchForExactItem` restent actifs car encore references par les hooks/tests et par des chemins non encore refactorises.

Pourquoi c'est important :

- Avant cette coupe, les anciens handlers n'etaient plus appeles, mais restaient compiles.
- Maintenant, ils ne peuvent plus revenir dans le runtime par accident.
- Cela rapproche la structure de l'objectif : un flux canonique source-backed unique, avec l'ancien code conserve seulement comme reference temporaire.
- La coupe reste prudente : elle retire du code actif les blocs les plus dangereux, sans casser les helpers encore necessaires a la transition.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Legacy_standalone_topic_rag_is_no_longer_called_from_main_turn_flow`
  - verifie toujours qu'il ne reste qu'une occurrence du handler standalone ;
  - verifie maintenant que cette occurrence est sous `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- `SourceBackedRagArchitectureTests.Legacy_documentary_probe_is_no_longer_called_from_main_turn_flow`
  - meme controle pour l'ancien documentary probe.
- `SourceBackedRagArchitectureTests.Legacy_exact_item_and_version_shortcuts_are_no_longer_called_from_main_turn_flow`
  - meme controle pour exact-item et version traceability.
- Nouveau test `SourceBackedRagArchitectureTests.Legacy_rag_fallbacks_are_not_enabled_by_default_in_build_files`
  - inspecte les `.csproj`, `.props` et `.targets` ;
  - verifie que `SAAIA_LEGACY_RAG_FALLBACKS` n'est pas defini par defaut.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 31/31.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Verification structure :
  - `ToolAgentOrchestrator.SourceBackedRag.cs` : 210 lignes.
  - `ToolAgentOrchestrator.SourceBackedRagFallbacks.cs` : 153 lignes.
  - `ToolAgentOrchestrator.SourceBackedRagTerminal.cs` : 148 lignes.
  - `ToolAgentOrchestrator.SourceBackedRagToolExecutor.cs` : 29 lignes.
  - tous les fichiers du dossier `SourceBackedRag` restent sous 500 lignes.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Statut apres cette coupe :

- Les anciens chemins standalone, documentary probe, exact-item et version traceability ne sont plus seulement non appeles : ils sont hors compilation active.
- Le pipeline canonique source-backed reste le chemin actif pour ces cas.
- La dette restante principale est maintenant :
  - continuer a extraire ou neutraliser les branches legacy post-tools/finalizers encore presentes dans le monolithe ;
  - separer progressivement les helpers generiques encore utiles dans des modules courts ;
  - faire la validation runtime/live puis le vrai test UI client.

## Mise a jour - 2026-07-09 - handlers RAG legacy deplaces physiquement dans OLD

Nouvelle coupe realisee :

- La coupe precedente mettait les handlers legacy hors compilation active, mais ils restaient physiquement dans `ToolAgentOrchestrator.cs`.
- Ils sont maintenant extraits dans :
  - `client/SAAIA.Client.WinUI/ToolAgent/OLD/ToolAgentOrchestrator.LegacyRagFallbackHandlers.20260709.cs`.
- Le fichier actif `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.cs` ne contient plus les signatures suivantes :
  - `TryHandleDocumentaryProbeAsync` ;
  - `TryHandleDocumentVersionTraceabilityLegacyShortcutAsync` ;
  - `TryHandleExactItemLegacyShortcutAsync` ;
  - `TryHandleStandaloneTopicRagAsync`.
- L'archive OLD garde le contenu complet sous `#if SAAIA_LEGACY_RAG_FALLBACKS`, dans une partial class de reference.
- Le projet exclut deja `ToolAgent/OLD/**/*.cs` de la compilation via `SAAIA.Client.WinUI.csproj`.

Pourquoi c'est important :

- On n'a plus seulement une neutralisation compile-time dans le monolithe.
- Le fichier actif est desormais allege d'environ 1 600 lignes de handlers historiques.
- L'ancien code reste consultable pour reprise/audit, mais il est clairement classe comme reference legacy hors compilation.
- Cette coupe est plus conforme a l'objectif utilisateur : garder une structure propre, lisible, sans rescapés d'anciens essais dans le chemin actif.

Validation ajoutee/adaptee :

- Les tests d'architecture ont ete adaptes :
  - ils exigent maintenant `0` occurrence des anciens handlers dans `ToolAgentOrchestrator.cs` ;
  - ils verifient que les signatures sont presentes uniquement dans l'archive OLD ;
  - ils conservent le controle que `SAAIA_LEGACY_RAG_FALLBACKS` n'est pas defini dans les fichiers de build.
- Le test `Legacy_toolagent_old_folder_is_reference_only` continue de valider que `ToolAgent/OLD/**/*.cs` est retire de `Compile` et garde en `None`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 31/31.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Verification ciblée :
  - `ToolAgentOrchestrator.cs` : 17 460 lignes apres extraction.
  - `ToolAgentOrchestrator.LegacyRagFallbackHandlers.20260709.cs` : 1 648 lignes dans OLD.
  - les signatures legacy ne sont plus trouvees dans le fichier actif.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Note git importante :

- `AUDIT-2026-07-09-rag-source-backed-clean-refactor.md`, les tests `SourceBackedRag*`, le dossier `SourceBackedRag/` et le dossier `ToolAgent/OLD/` sont encore non suivis dans ce worktree.
- `git diff --stat` ne montre donc pas ces fichiers non suivis par defaut ; utiliser `git status --short` pour les voir.

Statut apres cette coupe :

- Les quatre anciens handlers RAG concurrents sont maintenant archives dans OLD et absents du fichier actif.
- Le pipeline canonique reste le chemin actif et verifie par tests.
- Le monolithe reste trop gros ; la prochaine etape propre est de continuer a extraire les helpers generiques encore actifs et de neutraliser progressivement les finalizers/post-tools historiques qui n'ont plus leur place dans le flux canonique.

## Mise a jour - 2026-07-09 - extraction des helpers writer/RAG compaction

Nouvelle coupe realisee :

- Extraction comportementalement neutre d'une grosse grappe de helpers writer/RAG hors de `ToolAgentOrchestrator.cs`.
- Trois nouveaux partials specialises ont ete crees :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.WriterPromptBudget.cs`
    - budgets de prompt writer ;
    - retry compact apres overflow contexte ;
    - construction du bloc de resultats outil pour le writer.
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.WriterRagCompaction.cs`
    - compactage des resultats RAG pour le writer ;
    - compactage des hits, metas, provenance, contexte, cartes de contenu.
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.WriterRagRanking.cs`
    - detection overflow contexte LLM ;
    - deduplication/ranking des hits RAG pour le writer ;
    - preservation des meilleurs hits backend/source.
- `ToolAgentOrchestrator.cs` est descendu a 15 721 lignes.
- Les nouveaux fichiers restent sous 1 000 lignes :
  - `WriterPromptBudget.cs` : 571 lignes ;
  - `WriterRagCompaction.cs` : 758 lignes ;
  - `WriterRagRanking.cs` : 455 lignes.

Pourquoi c'est important :

- Le monolithe contenait encore une grappe importante de code auxiliaire qui n'avait pas besoin d'etre dans le flux principal.
- Cette extraction ne change pas la logique metier ni le role du LLM ; elle clarifie la structure.
- Elle rapproche le code de la cible utilisateur : fichiers par responsabilite, pas de fichier geant rempli de couches d'essais historiques.
- Elle permet aussi de mieux distinguer :
  - orchestration active ;
  - pipeline source-backed canonique ;
  - preparation du prompt writer ;
  - compactage/ranking des preuves RAG envoyees au writer.

Validation ajoutee :

- Nouveau test `SourceBackedRagArchitectureTests.Extracted_writer_rag_partials_stay_ordered_and_out_of_main_orchestrator`.
- Le test verifie :
  - l'existence des trois nouveaux partials ;
  - leur taille sous 1 000 lignes ;
  - l'absence des definitions extraites dans `ToolAgentOrchestrator.cs`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 32/32.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Note git importante :

- Les trois nouveaux partials writer/RAG sont encore non suivis dans ce worktree.
- `git diff --stat` ne les affiche donc pas tant qu'ils ne sont pas ajoutes a l'index.
- `git status --short` montre bien :
  - `ToolAgentOrchestrator.WriterPromptBudget.cs` ;
  - `ToolAgentOrchestrator.WriterRagCompaction.cs` ;
  - `ToolAgentOrchestrator.WriterRagRanking.cs`.

Statut apres cette coupe :

- Le monolithe est encore trop volumineux, mais il a ete allege d'une grappe coherente de helpers writer/RAG.
- Les nouveaux partials sont suffisamment courts pour etre lisibles et testables par garde-fou d'architecture.
- La prochaine dette structurelle reste la meme : traiter les finalizers/post-tools historiques encore actifs et continuer a isoler les helpers generiques par responsabilite.

## Mise a jour - 2026-07-09 - helpers documentary probe morts archives dans OLD

Nouvelle coupe realisee :

- Apres le deplacement du vieux handler documentary probe dans OLD, certains helpers associes n'etaient plus utilises par le code actif.
- Ces helpers ont ete extraits de `ToolAgentOrchestrator.cs` et archives dans :
  - `client/SAAIA.Client.WinUI/ToolAgent/OLD/ToolAgentOrchestrator.LegacyDocumentaryProbeHelpers.20260709.cs`.
- Helpers archives :
  - `RunDocumentaryProbeRetrievalAsync` ;
  - `BuildProbeRagToolResults(JsonElement result, string toolName)` ;
  - `SelectDocumentaryProbeClarificationHits` ;
  - `BuildDocumentaryProbeClarification(IReadOnlyList<RagHitSummary> hits, string language)` ;
  - `BuildDocumentaryProbeClarification(IReadOnlyList<RagItem> hits, string language)`.
- Les helpers encore utiles au flux actif sont restes dans le fichier actif :
  - `ResolveDocumentaryProbeTopK` ;
  - `ShouldExpandDocumentaryProbeRetrieval` ;
  - `ShouldUseWriterForDocumentaryProbeAnswer` ;
  - `IsBetterDocumentaryProbeCoverage` ;
  - `BuildDocumentaryProbeRetrievalQueries` ;
  - `BuildProbeRagToolResults(IReadOnlyList<RagItem> hits)`.

Pourquoi c'est important :

- Cette coupe retire du code mort lie a l'ancien documentary probe sans casser les helpers generiques encore utiles.
- Elle evite de garder des restes d'ancien chemin simplement parce qu'ils etaient proches de helpers encore actifs.
- Elle continue la logique voulue : ancien chemin consultable dans OLD, code actif limite aux morceaux encore justifies.

Validation ajoutee :

- Nouveau test `SourceBackedRagArchitectureTests.Legacy_documentary_probe_runtime_helpers_are_archived_out_of_main_orchestrator`.
- Le test verifie :
  - l'absence de ces helpers morts dans `ToolAgentOrchestrator.cs` ;
  - leur presence dans l'archive OLD.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 33/33.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.
- Verification structure :
  - `ToolAgentOrchestrator.cs` : 15 531 lignes.
  - `ToolAgentOrchestrator.LegacyDocumentaryProbeHelpers.20260709.cs` : 209 lignes dans OLD.

Statut apres cette coupe :

- Les handlers legacy et leurs helpers documentary-probe morts sont archives hors fichier actif.
- Les helpers generiques encore utiles restent actifs et couverts indirectement par les tests existants.
- La prochaine dette de fond reste les finalizers/post-tools historiques actifs et les grosses grappes de helpers generiques encore dans `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-09 - reprise canonique apres expansion legacy

Nouvelle coupe realisee :

- Ajout d'un deuxieme passage par `TryHandlePostToolRagResultsSourceBackedRagPipelineAsync` apres :
  - `TryExpandSourceBackedEvidenceRetrievalAsync` ;
  - `TryExpandBackendGuidanceClarificationRetrievalAsync`.
- Nouveau point d'entree trace :
  - `post_legacy_expansion_rag_results`.
- `TryHandlePostToolRagResultsSourceBackedRagPipelineAsync` accepte maintenant un `entryPoint` optionnel pour distinguer :
  - `post_tools_rag_results` ;
  - `post_legacy_expansion_rag_results`.

Pourquoi c'est important :

- Avant cette coupe, le premier garde-fou canonique passait avant l'expansion legacy.
- Mais l'expansion legacy pouvait ensuite ajouter des resultats `rag.search` ou `rag.multi_search`.
- Ces resultats pouvaient alors descendre vers les anciens blocs :
  - `TryBuildNoRagEvidenceAnswerForEmptySearch` ;
  - `pre_writer_source_backed` ;
  - outer finalizers / writer guards historiques.
- Maintenant, si l'expansion legacy produit du RAG, ces resultats repartent immediatement dans le pipeline canonique :
  - `EvidenceBundleBuilder` ;
  - LLM evidence judge ;
  - writer ;
  - source verifier ;
  - repair ;
  - UI source cards.

Ce que cela ne fait pas encore :

- L'expansion legacy existe encore.
- Elle devra etre supprimee ou remplacee par une orchestration LLM plus propre dans une prochaine coupe.
- Mais elle ne peut plus alimenter directement les anciens finalizers si elle produit du RAG dans le flux principal.

Validation ajoutee :

- Test d'architecture renforce :
  - `SourceBackedRagArchitectureTests.Post_tool_and_post_expansion_rag_results_reach_canonical_pipeline_before_legacy_source_backed_finalizers`.
- Le test verifie :
  - un premier passage canonique avant l'expansion legacy ;
  - un deuxieme passage canonique apres l'expansion legacy/backend-guidance ;
  - le deuxieme passage avant `TryBuildNoRagEvidenceAnswerForEmptySearch` ;
  - le deuxieme passage avant `pre_writer_source_backed`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 33/33.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Statut apres cette coupe :

- Les resultats RAG initiaux et les resultats RAG produits par l'expansion legacy du flux principal repassent maintenant par le pipeline canonique avant tout finalizer historique.
- La dette restante prioritaire est de remplacer/supprimer l'expansion legacy elle-meme, puis de traiter les finalizers/writer guards historiques encore presents dans `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-09 - expansion source-backed legacy retiree du flux principal

Nouvelle coupe realisee :

- Suppression de l'appel direct a `TryExpandSourceBackedEvidenceRetrievalAsync` dans le flux principal post-tools de `ToolAgentOrchestrator.cs`.
- Suppression de l'appel direct a `TryExpandBackendGuidanceClarificationRetrievalAsync` dans ce meme flux principal.
- Suppression du deuxieme garde-fou `post_legacy_expansion_rag_results`, devenu inutile puisque l'expansion legacy ne tourne plus a cet endroit.
- Ajout d'une trace explicite :
  - `source_backed_legacy_expansion.skipped`
  - raison : `canonical_pipeline_owns_rag_retrieval`.

Pourquoi c'est important :

- La coupe precedente empechait les resultats RAG produits par l'expansion legacy de descendre vers les anciens finalizers.
- Cette nouvelle coupe va plus loin : l'expansion legacy ne s'execute plus dans le flux principal.
- Cela rapproche le runtime de la cible :
  - le LLM planner/orchestrateur choisit les recherches ;
  - le pipeline canonique construit l'`EvidenceBundle` ;
  - le LLM evidence judge decide si les preuves repondent a la demande ;
  - le code ne lance plus une exploration deterministe post-tools pour essayer de sauver une reponse source-backed.

Ce qui reste volontairement en place :

- La methode `TryExpandSourceBackedEvidenceRetrievalAsync` existe encore.
- Elle reste appelee par `TryExpandSourceBackedEvidenceAfterCandidateAdjudicationAsync`, c'est-a-dire dans une boucle ou le LLM/candidate adjudication demande explicitement plus de retrieval.
- Cette zone devra etre reetudiee plus tard, mais elle n'est plus le filet deterministe du flux principal.

Validation adaptee :

- Le test d'architecture est devenu :
  - `SourceBackedRagArchitectureTests.Main_post_tool_flow_skips_legacy_source_backed_expansion_before_finalizers`.
- Il verifie :
  - un seul passage canonique post-tools dans le flux principal ;
  - la trace de skip legacy apres ce passage ;
  - aucune occurrence de `TryExpandSourceBackedEvidenceRetrievalAsync(` dans la portion principale avant `TryBuildNoRagEvidenceAnswerForEmptySearch` ;
  - aucune occurrence de `TryExpandBackendGuidanceClarificationRetrievalAsync(` dans cette meme portion ;
  - la trace de skip avant no-evidence et avant `pre_writer_source_backed`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 33/33.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Statut apres cette coupe :

- Le flux principal ne lance plus l'expansion source-backed legacy.
- Les resultats RAG issus du routeur ou des fallbacks source-backed passent par le pipeline canonique.
- La prochaine dette prioritaire est de traiter les finalizers/writer guards historiques encore presents, puis de reetudier l'expansion restante appelee depuis l'adjudication writer.

## Mise a jour - 2026-07-09 - pre-writer source-backed legacy hors compilation active

Nouvelle coupe realisee :

- Le bloc historique `pre_writer_source_backed` est maintenant place derriere :
  - `#if SAAIA_LEGACY_RAG_FINALIZERS`.
- Ce symbole n'est pas defini par defaut.
- Le bloc reste consultable dans `ToolAgentOrchestrator.cs` pour l'instant, mais il n'est plus compile ni executable.

Pourquoi c'est important :

- Ce bloc pouvait construire une reponse source-backed avant le writer.
- Il contenait des chemins deterministes comme :
  - category overview ;
  - countdown/planning fallback ;
  - extractive answer ;
  - option answer ;
  - fallback evidence answer ;
  - finalizer pre-writer.
- Meme si le flux canonique etait deja place avant lui, ce bloc restait un ancien chemin de decision source-backed dans le code actif.
- Le sortir de la compilation rapproche le flux principal de la cible :
  - pas de reponse source-backed produite par un vieux bloc pre-writer ;
  - le writer et le pipeline canonique gardent la responsabilite de produire/verifier la reponse.

Validation ajoutee :

- Nouveau test :
  - `SourceBackedRagArchitectureTests.Legacy_pre_writer_source_backed_return_is_out_of_active_compilation`.
- Le test verifie :
  - la presence du bloc legacy ;
  - le fait qu'il soit sous `SAAIA_LEGACY_RAG_FINALIZERS` ;
  - le fait que le writer stage reste hors de ce guard.
- Le test `Legacy_rag_fallbacks_are_not_enabled_by_default_in_build_files` verifie maintenant deux symboles :
  - `SAAIA_LEGACY_RAG_FALLBACKS` ;
  - `SAAIA_LEGACY_RAG_FINALIZERS`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 34/34.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier :
  - seules occurrences cuisine restantes dans les tests source-backed cibles : assertions `Assert.DoesNotContain("cuisine", ...)`.

Statut apres cette coupe :

- Le flux principal ne lance plus l'expansion source-backed legacy.
- Le vieux retour `pre_writer_source_backed` n'est plus compile.
- Les anciens handlers/fallbacks deja archives restent hors compilation.
- La dette restante prioritaire est maintenant :
  - outer finalizers / last-mile finalizers ;
  - writer/post-critic guards historiques ;
  - l'expansion restante utilisee depuis l'adjudication writer.

## Mise a jour - 2026-07-09 - outer finalizers source-backed legacy hors compilation active

Nouvelle coupe realisee :

- Les deux finalizers historiques suivants sont maintenant places derriere :
  - `#if SAAIA_LEGACY_RAG_FINALIZERS`.
- Finalizers concernes :
  - `outer-finalizer` ;
  - `outer-last-mile-finalizer`.
- Le symbole `SAAIA_LEGACY_RAG_FINALIZERS` n'est pas defini par defaut dans les fichiers de build.
- Ces chemins restent consultables dans `ToolAgentOrchestrator.cs`, mais ils ne sont plus compiles ni executables dans le runtime standard.

Correction importante pendant la coupe :

- Un premier placement du guard legacy avait accidentellement englobe une partie du bloc actif de reparation writer.
- Le placement a ete corrige pour obtenir cette structure :
  - `outer-finalizer` sous guard legacy ;
  - reparation writer source-backed toujours active ;
  - `outer-last-mile-finalizer` sous guard legacy separe.
- Cela evite de supprimer involontairement une etape active utile pendant qu'on neutralise les vieux finalizers.

Pourquoi c'est important :

- Ces finalizers etaient encore capables de reconstruire une reponse source-backed apres le writer.
- Ils utilisaient une logique historique de finalisation/reconstruction qui concurrencait le pipeline canonique.
- Les mettre hors compilation reduit le nombre de decideurs concurrents dans le flux principal.
- La trajectoire reste :
  - le LLM planner/orchestrateur decide des recherches ;
  - le pipeline canonique construit l'`EvidenceBundle` ;
  - le LLM evidence judge decide si les preuves repondent a la demande ;
  - le writer produit la reponse ;
  - le code verifie mecaniquement les citations/sources et repare uniquement le contrat.

Validation ajoutee :

- Nouveau test :
  - `SourceBackedRagArchitectureTests.Active_writer_structured_repair_stays_outside_legacy_finalizer_guards`.
- Ce test verifie que la trace active :
  - `writer.structured_support_repair.start`
  reste hors des guards `SAAIA_LEGACY_RAG_FINALIZERS`.
- Le test existant :
  - `SourceBackedRagArchitectureTests.Legacy_outer_source_backed_finalizers_are_out_of_active_compilation`
  verifie maintenant que les deux finalizers externes sont bien sous guard legacy.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
  - Note : une premiere tentative a depasse la limite de 2 minutes sans erreur de compilation affichee ; les processus restants ont ete arretes puis la commande a ete relancee avec un delai plus long. Build final OK en 3 min 56 s.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 36/36.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif :
  - `rg -n "cuisine|meal|repas|d[iî]ner|déjeuner|petit-déjeuner|breakfast|lunch|dinner" client\SAAIA.Client.WinUI\ToolAgent -g "*.cs" -g "!OLD/**"`
  - aucune occurrence.

Statut apres cette coupe :

- Les anciens handlers/fallbacks RAG principaux sont archives ou hors compilation.
- Le vieux retour `pre_writer_source_backed` est hors compilation.
- Les finalizers externes `outer-finalizer` et `outer-last-mile-finalizer` sont hors compilation.
- La reparation writer active reste compilee et testee.
- La dette restante prioritaire devient :
  - auditer les writer/post-critic guards historiques encore actifs ;
  - reetudier l'expansion restante appelee depuis l'adjudication writer ;
  - continuer a reduire `ToolAgentOrchestrator.cs` en extrayant ou archivant les responsabilites legacy ;
  - lancer ensuite le vrai test UI client quand le chemin canonique sera suffisamment nettoye.

## Mise a jour - 2026-07-09 - absolute finalizer AnswerAsync hors compilation active

Nouvelle coupe realisee :

- Le finalizer de derniere minute dans `AnswerAsync` est maintenant place derriere :
  - `#if SAAIA_LEGACY_RAG_FINALIZERS`.
- Bloc concerne :
  - `writer.absolute_finalizer.start` ;
  - appel a `TryFinalizeSourceBackedPlanningResponse(...)` ;
  - `answer-async-finalizer` ;
  - reecriture eventuelle de `finalAnswer`, `sources` et `_lastAnswerSource`.
- Le runtime actif conserve une trace explicite :
  - `writer.absolute_finalizer.skipped`
  - raison : `legacy_finalizer_disabled`.

Pourquoi c'est important :

- Meme apres le writer et les guards post-writer, ce bloc pouvait encore rappeler l'ancien finalizer source-backed.
- Il representait donc un dernier decideur historique capable de re-finaliser la reponse hors du pipeline canonique.
- Apres cette coupe, les appels restants a `TryFinalizeSourceBackedPlanningResponse(...)` dans `ToolAgentOrchestrator.cs` sont tous sous `SAAIA_LEGACY_RAG_FINALIZERS`.
- Cela clarifie la responsabilite :
  - le pipeline canonique et le writer produisent la reponse ;
  - les guards actifs verifient/reparent le contrat ;
  - les vieux finalizers ne peuvent plus reprendre la main dans le runtime standard.

Validation ajoutee/adaptee :

- Le test `Legacy_outer_source_backed_finalizers_are_out_of_active_compilation` verifie maintenant aussi :
  - `writer.absolute_finalizer.start` sous guard legacy ;
  - presence de la trace active `writer.absolute_finalizer.skipped` ;
  - tous les appels `TryFinalizeSourceBackedPlanningResponse(` dans `ToolAgentOrchestrator.cs` sont sous guard `SAAIA_LEGACY_RAG_FINALIZERS`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 36/36.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Les finalizers source-backed historiques encore presents dans `ToolAgentOrchestrator.cs` sont hors compilation active.
- Le runtime garde une trace de skip pour diagnostiquer le fait qu'ils sont volontairement desactives.
- La prochaine dette se deplace vers les guards post-writer encore actifs qui fabriquent des fallbacks/reconstructions deterministes :
  - `writer_guard_*` ;
  - `post_critic_guard_*` ;
  - `post_writer_guard_*`.

## Mise a jour - 2026-07-09 - bypass writer source-backed deterministe hors compilation active

Nouvelle coupe realisee :

- Le bloc `writer.bypass` avec raison :
  - `source_backed_deterministic`
  est maintenant place derriere :
  - `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- Ce bloc pouvait eviter completement le writer et produire une reponse par code via :
  - `BuildSourceBackedCountdownPlanningAnswer(...)` ;
  - `BuildSourceBackedExtractiveAnswer(...)` ;
  - `BuildSourceBackedOptionAnswer(...)` ;
  - `BuildRagEvidenceFallbackAnswer(...)`.
- Le runtime actif conserve seulement une trace :
  - `writer.bypass.skipped`
  - raison : `legacy_source_backed_deterministic_disabled`.

Pourquoi c'est important :

- Ce bloc etait un decideur semantique code avant le writer.
- Il pouvait prendre les resultats RAG et fabriquer directement une reponse source-backed sans laisser le LLM finaliser.
- Le retirer du runtime actif respecte mieux la cible :
  - le writer LLM reste l'etape de synthese ;
  - le code ne fabrique pas une reponse finale a partir de heuristiques de type countdown/option/extractive ;
  - les traces indiquent clairement quand ce bypass aurait ete eligible mais a ete volontairement desactive.

Validation ajoutee :

- Nouveau test :
  - `SourceBackedRagArchitectureTests.Legacy_source_backed_deterministic_writer_bypass_is_out_of_active_compilation`.
- Le test verifie :
  - le marker `source_backed_deterministic` est dans la branche inactive `SAAIA_LEGACY_RAG_FALLBACKS` ;
  - la trace active `legacy_source_backed_deterministic_disabled` existe.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 37/37.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Les finalizers historiques sont hors compilation active.
- Le bypass writer deterministe source-backed est hors compilation active.
- Le writer reprend la main quand ce bypass aurait ete eligible.
- La prochaine dette prioritaire reste les guards post-writer encore actifs qui reconstruisent/fallbackent par code apres une sortie writer ou critic :
  - `writer_guard_*` ;
  - `post_critic_guard_*` ;
  - `post_writer_guard_*`.

## Mise a jour - 2026-07-09 - reconstruction deterministe du final structured gate hors compilation active

Nouvelle coupe realisee :

- La branche :
  - `writer.final_structured_gate.deterministic.start`
  est maintenant placee derriere :
  - `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- Cette branche appelait :
  - `TryBuildSupportedStructuredPlanningAnswer(...)`
  pour reconstruire une reponse structuree a partir des candidats sources.
- Le runtime actif ne reconstruit plus cette reponse par code.
- A la place, quand le writer/repair n'a pas produit une reponse suffisamment supportee, le runtime trace :
  - `writer.final_structured_gate.deterministic.skipped`
  - raison : `legacy_structured_planning_deterministic_rebuild_disabled`
  puis retourne une reponse d'insuffisance source-backed traçable.

Pourquoi c'est important :

- Cette branche etait une reconstruction deterministe apres le writer.
- Elle pouvait transformer un ensemble de candidats en reponse finale sans repasser par une decision de synthese LLM.
- Le code garde le droit de constater un echec de contrat source-backed et de refuser/indiquer l'insuffisance.
- Il ne reconstruit plus une reponse finale structuree a la place du LLM dans ce gate.

Validation ajoutee :

- Nouveau test :
  - `SourceBackedRagArchitectureTests.Legacy_structured_final_gate_deterministic_rebuild_is_out_of_active_compilation`.
- Le test verifie :
  - le marker `writer.final_structured_gate.deterministic.start` est dans la branche inactive `SAAIA_LEGACY_RAG_FALLBACKS` ;
  - la trace active `legacy_structured_planning_deterministic_rebuild_disabled` existe.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Les finalizers historiques restent hors compilation active.
- Le bypass writer deterministe reste hors compilation active.
- La reconstruction deterministe du final structured gate est hors compilation active.
- Le code actif peut refuser pour insuffisance de sources, mais ne reconstruit plus cette reponse finale structuree dans ce gate.
- Prochaine cible logique :
  - les branches `writer_guard_*` et `post_critic_guard_*` qui font encore des fallback/rebuilds deterministes apres sortie writer/critic.

## Mise a jour - 2026-07-09 - fallback deterministe post-critic poor-planning hors compilation active

Nouvelle coupe realisee :

- Dans le bloc `post_critic_guard_poor_planning_*`, la tentative de repair LLM reste active.
- Si le repair LLM echoue, les anciens fallbacks deterministes suivants sont maintenant derriere :
  - `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- Branches legacy concernees :
  - `post_critic_guard_poor_planning_clean_fallback` ;
  - `post_critic_guard_poor_planning_deterministic`.
- Le runtime actif trace maintenant :
  - `post_critic_guard.poor_planning_deterministic.skipped`
  - raison : `legacy_post_critic_deterministic_fallback_disabled`
  puis retourne une reponse d'insuffisance source-backed traçable.

Pourquoi c'est important :

- Le critic peut signaler ou aider a corriger une reponse.
- Le repair par LLM reste coherent avec la cible.
- En revanche, quand ce repair echouait, le code reconstruisait encore une reponse "propre" ou un planning partiel par heuristiques.
- Cette coupe retire ce dernier comportement deterministe pour ce chemin precis.

Validation ajoutee :

- Nouveau test :
  - `SourceBackedRagArchitectureTests.Legacy_post_critic_poor_planning_deterministic_fallback_is_out_of_active_compilation`.
- Le test verifie :
  - `post_critic_guard_poor_planning_clean_fallback` est dans la branche inactive `SAAIA_LEGACY_RAG_FALLBACKS` ;
  - `post_critic_guard_poor_planning_deterministic` est dans la branche inactive `SAAIA_LEGACY_RAG_FALLBACKS` ;
  - la trace active `legacy_post_critic_deterministic_fallback_disabled` existe.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 39/39.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Le repair LLM post-critic reste actif.
- Les reconstructions deterministes post-critic poor-planning sont hors compilation active.
- La prochaine cible prioritaire est le reste des guards post-writer/post-critic qui reconstruisent encore des plannings ou fallbacks par code :
  - `post_critic_guard_unsupported_planning_items_*` ;
  - `post_writer_guard_control_leak_deterministic` ;
  - `post_writer_guard_planning_rebuilt_from_supported_candidates`.

## Mise a jour - 2026-07-09 - rebuild post-critic unsupported planning hors compilation active

Nouvelle coupe realisee :

- La branche `post_critic_guard_unsupported_planning_items_*` est maintenant separee :
  - ancien rebuild deterministe derriere `#if SAAIA_LEGACY_RAG_FALLBACKS` ;
  - branche active = trace + insuffisance source-backed.
- Branche legacy concernee :
  - `post_critic_guard_unsupported_planning_items_source_backed`.
- Le runtime actif trace :
  - `post_critic_guard.unsupported_planning_deterministic.skipped`
  - raison : `legacy_post_critic_unsupported_planning_rebuild_disabled`.

Pourquoi c'est important :

- Cette branche ne faisait pas de repair LLM.
- Elle reconstruisait directement un planning source-backed via `BuildSourceBackedPlanningDraft(...)`.
- C'etait donc un chemin ou le code redevenait clairement le producteur semantique de la reponse.
- Apres cette coupe, le code peut encore refuser pour insuffisance, mais il ne fabrique plus le planning dans ce chemin post-critic.

Validation ajoutee :

- Nouveau test :
  - `SourceBackedRagArchitectureTests.Legacy_post_critic_unsupported_planning_rebuild_is_out_of_active_compilation`.
- Le test verifie :
  - `post_critic_guard_unsupported_planning_items_source_backed` est dans la branche inactive `SAAIA_LEGACY_RAG_FALLBACKS` ;
  - la trace active `legacy_post_critic_unsupported_planning_rebuild_disabled` existe.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 40/40.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Les rebuilds deterministes post-critic poor-planning et unsupported-planning sont hors compilation active.
- Les repairs LLM restent actifs.
- Prochaine cible :
  - `post_writer_guard_control_leak_deterministic` ;
  - `post_writer_guard_planning_rebuilt_from_supported_candidates` ;
  - puis les `writer_guard_*` pre-critic encore deterministes.

## Mise a jour - 2026-07-09 - fallback post-writer control-leak hors compilation active

Nouvelle coupe realisee :

- Dans le bloc `post_writer_guard_control_leak_*`, la tentative de repair LLM reste active.
- Si le repair LLM echoue, l'ancien fallback deterministe est maintenant derriere :
  - `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- Branche legacy concernee :
  - `post_writer_guard_control_leak_deterministic`.
- Le runtime actif trace :
  - `post_writer_guard.control_leak_deterministic.skipped`
  - raison : `legacy_post_writer_control_leak_fallback_disabled`
  puis retourne une reponse d'insuffisance source-backed traçable.

Pourquoi c'est important :

- Le control-leak guard est utile pour detecter une sortie writer qui expose du vocabulaire interne.
- Le repair LLM est legitime : il demande au LLM de reformuler correctement.
- En revanche, le fallback deterministe apres echec du repair redevenait un producteur de reponse par code.
- Cette coupe garde la verification/reparation, mais retire la reconstruction deterministe.

Validation ajoutee :

- Nouveau test :
  - `SourceBackedRagArchitectureTests.Legacy_post_writer_control_leak_deterministic_fallback_is_out_of_active_compilation`.
- Le test verifie :
  - `post_writer_guard_control_leak_deterministic` est dans la branche inactive `SAAIA_LEGACY_RAG_FALLBACKS` ;
  - la trace active `legacy_post_writer_control_leak_fallback_disabled` existe.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Les fallbacks deterministes post-critic poor-planning, post-critic unsupported-planning et post-writer control-leak sont hors compilation active.
- Les repairs LLM correspondants restent actifs quand ils existent.
- Prochaine cible :
  - `post_writer_guard_planning_rebuilt_from_supported_candidates` ;
  - puis les `writer_guard_*` pre-critic encore deterministes.

## Mise a jour - 2026-07-09 - rebuild post-writer planning support hors compilation active

Nouvelle coupe realisee :

- Dans le final support check post-writer, l'analyse mecanique de support reste active.
- Si le support est insuffisant, le mode strict refusait deja sans reconstruction.
- Le mode non strict pouvait encore reconstruire une reponse via :
  - `BuildSourceBackedPlanningDraft(...)`.
- Cette reconstruction est maintenant derriere :
  - `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- Branche legacy concernee :
  - `post_writer_guard_planning_rebuilt_from_supported_candidates`.
- Le runtime actif trace :
  - `writer.final_support_check.deterministic_rebuild.skipped`
  - raison : `legacy_post_writer_planning_rebuild_disabled`
  puis retourne une insuffisance source-backed traçable.

Pourquoi c'est important :

- Le final support check est utile comme verification mecanique de contrat.
- Le probleme etait le rebuild qui transformait des candidats supportes en reponse finale.
- Cette coupe conserve la verification et le refus en cas d'insuffisance.
- Elle retire la generation deterministe de reponse finale dans ce chemin post-writer.

Validation ajoutee :

- Nouveau test :
  - `SourceBackedRagArchitectureTests.Legacy_post_writer_planning_rebuild_is_out_of_active_compilation`.
- Le test verifie :
  - `post_writer_guard_planning_rebuilt_from_supported_candidates` est dans la branche inactive `SAAIA_LEGACY_RAG_FALLBACKS` ;
  - la trace active `legacy_post_writer_planning_rebuild_disabled` existe.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 42/42.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Les principales reconstructions deterministes post-critic/post-writer identifiees sont hors compilation active.
- Les checks mecaniques et les repairs LLM restent actifs.
- Prochaine cible :
  - les `writer_guard_*` pre-critic encore deterministes ;
  - puis l'expansion restante appelee depuis l'adjudication writer.

## Mise a jour - 2026-07-09 - rebuilds writer_guard pre-critic hors compilation active

Nouvelle coupe realisee :

- Trois blocs `writer_guard_*` pre-critic ont ete traites.
- Dans chaque cas, la tentative de repair LLM reste active quand elle existe.
- Les reconstructions/fallbacks deterministes sont maintenant derriere :
  - `#if SAAIA_LEGACY_RAG_FALLBACKS`.

Branches legacy concernees :

- `writer_guard_underused_planning_sources`.
- `writer_guard_poor_planning_clean_fallback`.
- `writer_guard_poor_planning_deterministic`.
- `writer_guard_unsupported_planning_items_source_backed`.

Branches actives ajoutees :

- `writer_guard.underused_planning_deterministic.skipped`
  - raison : `legacy_writer_guard_underused_planning_rebuild_disabled`.
- `writer_guard.poor_planning_deterministic.skipped`
  - raison : `legacy_writer_guard_poor_planning_fallback_disabled`.
- `writer_guard.unsupported_planning_deterministic.skipped`
  - raison : `legacy_writer_guard_unsupported_planning_rebuild_disabled`.

Pourquoi c'est important :

- Ces guards sont places avant le critic.
- Ils pouvaient corriger une reponse writer jugee pauvre/insuffisante en reconstruisant directement une reponse source-backed par code.
- Les repairs LLM restent dans le flux.
- Les rebuilds par `BuildSourceBackedPlanningDraft(...)`, `BuildSourceBackedSafeFallbackAnswer(...)`, `BuildNonPoorSourceBackedFallbackAnswer(...)` ou `BuildReadablePartialPlanningEvidenceAnswer(...)` ne sont plus actifs dans ces chemins.

Validation ajoutee :

- Nouveaux tests :
  - `Legacy_writer_guard_underused_planning_rebuild_is_out_of_active_compilation`.
  - `Legacy_writer_guard_poor_planning_deterministic_fallback_is_out_of_active_compilation`.
  - `Legacy_writer_guard_unsupported_planning_rebuild_is_out_of_active_compilation`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 45/45.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Les principaux rebuilds/fallbacks deterministes writer_guard pre-critic sont hors compilation active.
- Les repairs LLM restent actifs.
- Prochaine cible :
  - les guards pre-critic qui produisent encore des reponses de blocage/anchor par code ;
  - l'expansion restante appelee depuis l'adjudication writer.

## Mise a jour - 2026-07-09 - guards anchor/overpromoted pre-critic hors compilation active

Nouvelle coupe realisee :

- Les guards pre-critic qui produisaient des reponses de blocage/anchor par code sont maintenant derriere :
  - `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- Branches legacy concernees :
  - `writer_guard_missing_required_evidence` ;
  - `writer_guard_missing_pairing_anchor` ;
  - `writer_guard_missing_broad_anchor`.
- Le runtime actif trace :
  - `writer_guard.anchor_deterministic_checks.skipped`
  - raison : `legacy_writer_guard_anchor_checks_disabled`.

Autre coupe dans le meme groupe :

- Le fallback deterministe :
  - `writer_guard_overpromoted_options`
  est maintenant derriere `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- Le runtime actif trace :
  - `writer_guard.overpromoted_options_deterministic.skipped`
  - raison : `legacy_writer_guard_overpromoted_options_fallback_disabled`
  puis retourne une insuffisance source-backed traçable.

Pourquoi c'est important :

- Les guards d'ancrage decidaient par code qu'une reponse devait etre remplacee par une reponse de blocage.
- Le guard overpromoted reconstruisait un fallback par code quand la reponse semblait trop ambitieuse par rapport aux sources.
- Ces comportements peuvent etre utiles comme diagnostics, mais pas comme producteurs de reponse finale dans l'architecture cible.
- Apres cette coupe, le flux actif laisse continuer writer/critic/verifications, ou refuse pour insuffisance sans reconstruire une synthese.

Validation ajoutee :

- Nouveaux tests :
  - `Legacy_writer_guard_anchor_checks_are_out_of_active_compilation`.
  - `Legacy_writer_guard_overpromoted_options_fallback_is_out_of_active_compilation`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 47/47.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Les principaux guards pre-critic qui produisaient des reponses par code sont hors compilation active.
- Il reste a traiter :
  - le fallback no-rag-data dans le post writer ;
  - l'expansion restante appelee depuis l'adjudication writer ;
  - puis une passe structurelle pour extraire/archiver proprement les blocs legacy sous guard au lieu de les laisser gonfler `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-09 - fallbacks no-rag-data et degenerate-output hors compilation active

Nouvelle coupe realisee :

- Le fallback post-writer `ShouldFallbackFromNoRagDataAnswer(...)` a ete modifie :
  - le repair LLM reste actif quand il est autorise ;
  - les alternatives deterministes sont derriere `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- Alternatives legacy concernees :
  - `BuildSourceBackedPlanningOrExtractiveAnswer(...)` ;
  - `BuildSourceBackedSafeFallbackAnswer(...)`.
- Le runtime actif trace :
  - `writer_guard.no_rag_data_deterministic.skipped`
  - raison : `legacy_writer_guard_no_rag_data_fallback_disabled`.

Deuxieme coupe dans le meme groupe :

- Le guard `writer_guard_degenerate_output` est maintenant derriere `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- Le runtime actif trace :
  - `writer_guard.degenerate_output_deterministic.skipped`
  - raison : `legacy_writer_guard_degenerate_output_fallback_disabled`.

Pourquoi c'est important :

- Ces branches intervenaient quand la reponse writer etait vide, generique ou degenerate.
- Le bon comportement cible est de tenter un repair LLM si possible, ou de refuser proprement.
- Le code ne doit plus reconstruire une reponse source-backed finale a partir de builders deterministes.

Validation ajoutee :

- Nouveaux tests :
  - `Legacy_writer_guard_no_rag_data_fallback_is_out_of_active_compilation`.
  - `Legacy_writer_guard_degenerate_output_fallback_is_out_of_active_compilation`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 49/49.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Les fallbacks no-rag-data et degenerate-output ne reconstruisent plus de reponse par code dans le runtime actif.
- Il reste un fallback countdown post-writer actif repere juste au-dessus de no-rag-data.
- Ensuite, la dette prioritaire devient :
  - l'expansion restante appelee depuis l'adjudication writer ;
  - l'extraction/archivage structurel des blocs legacy sous guard.

## Mise a jour - 2026-07-09 - fallback countdown post-writer hors compilation active

Nouvelle coupe realisee :

- Le fallback post-writer countdown est maintenant derriere :
  - `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- Branche legacy concernee :
  - `BuildSourceBackedCountdownPlanningAnswer(...)`.
- Le runtime actif trace :
  - `writer_guard.countdown_deterministic.skipped`
  - raison : `legacy_writer_guard_countdown_fallback_disabled`.

Pourquoi c'est important :

- Ce fallback n'etait pas un repair LLM.
- Il reconstruisait une reponse specialisee par code pour les demandes de planning/countdown.
- Il pouvait donc contourner la synthese writer et redevenir un producteur de reponse finale.
- Apres cette coupe, le writer garde la main ; le code trace simplement que l'ancien fallback aurait ete eligible.

Validation ajoutee :

- Nouveau test :
  - `SourceBackedRagArchitectureTests.Legacy_writer_guard_countdown_fallback_is_out_of_active_compilation`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 50/50.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Les fallbacks deterministes post-writer identifies sont hors compilation active.
- Le prochain sujet prioritaire devient l'expansion restante appelee depuis l'adjudication writer.
- Ensuite, il faudra nettoyer structurellement les blocs legacy sous guard pour ne pas laisser `ToolAgentOrchestrator.cs` accumuler du code mort.

## Mise a jour - 2026-07-09 - expansion RAG depuis candidate adjudication hors compilation active

Nouvelle coupe realisee :

- L'adjudication candidate du writer reste active.
- Si cette adjudication demande plus de retrieval, le runtime actif ne lance plus l'ancien expander :
  - `TryExpandSourceBackedEvidenceRetrievalAsync(...)`.
- L'appel a cet expander est maintenant derriere :
  - `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- Le runtime actif trace :
  - `writer.candidate_adjudication.retrieval_expansion.skipped`
  - raison : `canonical_pipeline_owns_retrieval`.

Pourquoi c'est important :

- L'adjudication candidate peut rester utile comme conseil prive pour le writer.
- Mais elle ne doit plus declencher une expansion RAG cachee via l'ancien expander.
- Le retrieval supplementaire doit etre gere par le pipeline canonique :
  - planner/orchestrateur ;
  - evidence judge ;
  - tool executor ;
  - EvidenceBundle ;
  - verifier/repair.

Validation ajoutee :

- Nouveau test :
  - `SourceBackedRagArchitectureTests.Legacy_candidate_adjudication_retrieval_expansion_is_out_of_active_compilation`.
- Le test verifie :
  - l'appel `var expanded = await TryExpandSourceBackedEvidenceRetrievalAsync` est dans la branche inactive `SAAIA_LEGACY_RAG_FALLBACKS` ;
  - la trace active `canonical_pipeline_owns_retrieval` existe.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 51/51.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Les vieux chemins d'expansion/fallback/finalizer qui reprenaient la main dans le flux source-backed sont neutralises dans le runtime actif.
- L'implementation de `TryExpandSourceBackedEvidenceRetrievalAsync(...)` reste encore dans `ToolAgentOrchestrator.cs`, mais son appel restant dans le flux writer est hors compilation active.
- Prochaine etape structurelle :
  - deplacer/extraire les blocs legacy sous guard vers `OLD` ou des fichiers legacy dedies ;
  - reduire la taille de `ToolAgentOrchestrator.cs` ;
  - verifier qu'il ne reste pas de gros producteur deterministe de reponse source-backed dans le runtime actif ;
  - preparer ensuite le test UI client reel.

## Mise a jour - 2026-07-09 - rebuilds structured support apres writer hors compilation active

Nouvelle coupe realisee :

- Deux rebuilds structurés apres writer sont maintenant derriere :
  - `#if SAAIA_LEGACY_RAG_FALLBACKS`.
- Branches legacy concernees :
  - `writer.structured_support_rebuild.accepted` ;
  - `writer.structured_support_repair.post_rebuild.start`.
- Le runtime actif conserve :
  - l'acceptation par citations visibles ;
  - l'analyse mecanique de support ;
  - le repair LLM ;
  - le retry LLM si necessaire.
- Si le repair LLM echoue, le runtime actif trace :
  - `writer.structured_support_repair.post_rebuild.skipped`
  - raison : `legacy_structured_support_post_rebuild_disabled`
  puis retourne une insuffisance source-backed traçable.

Pourquoi c'est important :

- Ces branches reconstruisaient une reponse structuree par code apres le writer.
- C'etait encore une maniere pour le backend client de redevenir producteur semantique.
- La coupe garde le controle mecanique et les repairs LLM, mais retire les rebuilds deterministes.

Validation ajoutee :

- Nouveau test :
  - `SourceBackedRagArchitectureTests.Legacy_structured_support_rebuilds_are_out_of_active_compilation`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 52/52.
- `git diff --check`
  - OK. Seulement les avertissements CRLF/LF deja presents dans le worktree.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Statut apres cette coupe :

- Les rebuilds structurés principaux apres writer/repair sont hors compilation active.
- Le pipeline actif garde les repairs LLM et les refus source-backed.
- Il reste maintenant a faire une passe structurelle pour sortir les blocs legacy sous guard de `ToolAgentOrchestrator.cs` autant que possible.

## Mise a jour - 2026-07-09 - suppression physique des blocs legacy sous preprocesseur

Nouvelle coupe realisee :

- `TryExpandSourceBackedEvidenceRetrievalAsync(...)` a ete extrait de `ToolAgentOrchestrator.cs`.
- Le code extrait est gare en reference uniquement dans :
  - `client/SAAIA.Client.WinUI/ToolAgent/OLD/ToolAgentOrchestrator.LegacySourceBackedEvidenceRetrieval.20260709.cs`.
- `ToolAgent/OLD/**/*.cs` reste exclu de la compilation active par le `.csproj`.
- Les hooks/tests orphelins qui ne servaient plus qu'a maintenir cette ancienne exploration ont ete retires :
  - `ResolveSourceBackedExplorationPassCategoryScopeForTests` ;
  - `ShouldTrustSourceBackedExplorationPassCategoryScopeForTests` ;
  - `BuildSourceBackedCandidateAdjudicationExpansionEnvelopeForTests` ;
  - `IsUsefulSourceBackedCandidateAdjudicationExpansionGainForTests`.
- Les helpers actifs associes a l'ancien envelope de relance cachee ont ete retires :
  - `IsLlmCandidateAdjudicationRetrievalExpansionEnvelope` ;
  - `BuildSourceBackedCandidateAdjudicationExpansionEnvelope` ;
  - `IsUsefulSourceBackedCandidateAdjudicationExpansionGain`.
- Les 20 blocs `#if SAAIA_LEGACY_RAG_FALLBACKS` / `#if SAAIA_LEGACY_RAG_FINALIZERS` restants dans `ToolAgentOrchestrator.cs` ont ete supprimes mecaniquement :
  - les branches legacy mortes ont ete enlevees ;
  - les branches actives `#else` ont ete conservees ;
  - le comportement runtime actif reste celui deja valide precedemment, mais sans code legacy cache dans le fichier principal.

Effet structurel :

- `ToolAgentOrchestrator.cs` est passe d'environ 14 403 lignes au debut de cette reprise a environ 11 982 lignes.
- Le fichier actif ne contient plus :
  - `SAAIA_LEGACY_RAG_FALLBACKS` ;
  - `SAAIA_LEGACY_RAG_FINALIZERS` ;
  - `#if`, `#else`, `#endif`.
- Les tests d'architecture ne verifient plus que l'ancien code est "sous guard" ; ils verifient maintenant que les branches legacy ont disparu du fichier actif.
- Le runtime actif garde les traces de skip utiles au diagnostic :
  - `source_backed_legacy_expansion.skipped` ;
  - `writer.structured_support_repair.post_rebuild.skipped` ;
  - `writer.absolute_finalizer.skipped` ;
  - `writer.candidate_adjudication.retrieval_expansion.skipped` avec raison `canonical_pipeline_owns_retrieval`.

Correction de test liee :

- `SourceBackedEvidencePlannerObservabilityTests.Llm_exploration_planner_removes_decorative_fillers_but_keeps_slot_queries` ne force plus le rejet de `repas menus`.
- Raison : `menus` n'est pas un filler decoratif generique universel ; le bloquer serait une regle de domaine fragile. Les vrais fillers generiques restent rejetes dans ce test (`idees`, `suggestions`, `exemples`).

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 36/36.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche active legacy/preprocesseur dans `ToolAgentOrchestrator.cs` :
  - aucune occurrence de `SAAIA_LEGACY_RAG_FALLBACKS`, `SAAIA_LEGACY_RAG_FINALIZERS`, `#if`, `#else`, `#endif`.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check`
  - OK sans erreur bloquante.
  - Avertissements CRLF/LF encore presents sur plusieurs fichiers du worktree, dont `ToolAgentOrchestrator.cs` et des fichiers deja signales.

Validation non terminee :

- `dotnet test ... --filter "FullyQualifiedName~StructuredPlanningCoverage|FullyQualifiedName~SourceBackedEvidencePlannerObservability|FullyQualifiedName~ApiClientDocumentsTransition"` a depasse 5 minutes.
- `dotnet test ... --filter "FullyQualifiedName~StructuredPlanningCoverage"` a aussi depasse 5 minutes.
- Les processus `dotnet/vstest` accroches par timeout ont ete arretes proprement.
- Ces timeouts ne prouvent pas un echec fonctionnel, mais la validation complete de cette famille reste a relancer de maniere plus fine ou avec un timeout plus long.

Statut apres cette coupe :

- Les vieux chemins legacy sous preprocesseur ne sont plus dans le fichier principal.
- L'ancien expander de retrieval est maintenant dans `OLD`, non compile.
- Le pipeline actif conserve le writer, les repairs LLM, les refus source-backed et les traces de skip.
- Il reste encore beaucoup de travail structurel hors de cette tranche :
  - `ToolAgentOrchestrator.State.cs` reste beaucoup trop gros ;
  - `ToolAgentOrchestrator.cs` reste encore trop gros ;
  - plusieurs traces `legacy_*_disabled` devraient probablement etre renommees ou consolidees en traces canoniques plus propres ;
  - la validation UI reelle client n'a pas encore ete executee dans cette tranche.

## Mise a jour - 2026-07-09 - extraction source refs / payload UI et traces canoniques

Nouvelle coupe realisee :

- La responsabilite "sources visibles / citations / payload UI source cards" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveaux fichiers actifs :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceRefs.cs` ;
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceCitationReconciliation.cs` ;
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourcePayload.cs`.
- Ces fichiers restent courts :
  - `SourceRefs` : environ 295 lignes ;
  - `SourceCitationReconciliation` : environ 527 lignes ;
  - `SourcePayload` : environ 641 lignes.
- `ToolAgentOrchestrator.State.cs` est passe d'environ 34 090 lignes au debut de la reprise a environ 32 658 lignes.

Ce qui a ete deplace :

- Derivation de sources visibles depuis les resultats RAG.
- Normalisation/deduplication des sources visibles.
- Reconciliation entre sources visibles et citations dans la reponse finale.
- Construction du payload UI des source cards.
- Helper partage `NullIfWhiteSpace`, conserve dans une partial active car utilise par plusieurs fichiers.

Nettoyage de traces :

- Les raisons de trace `legacy_*_disabled` restantes dans le runtime actif ont ete renommees en raisons canoniques.
- Exemples :
  - `canonical_writer_required_no_deterministic_bypass` ;
  - `canonical_structured_planning_no_deterministic_rebuild` ;
  - `canonical_source_contract_owns_finalization` ;
  - `canonical_pipeline_owns_retrieval`.
- Objectif : les traces diagnostiquent maintenant le pipeline cible au lieu de parler d'anciens chemins desactives.

Validation ajoutee :

- Nouveau test d'architecture :
  - `SourceBackedRagArchitectureTests.Extracted_source_payload_partials_stay_ordered_and_out_of_state_dump`.
- Le test verifie :
  - que les trois partials existent ;
  - qu'elles restent sous 800 lignes ;
  - que `State.cs` ne contient plus les methodes principales extraites.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 37/37.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche active des traces `legacy_*_disabled` et symboles legacy dans `ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche ciblee hardcoding metier dans le code actif `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche dans `State.cs` :
  - les methodes extraites `DeriveSourcesFromRagHits`, `ReconcileVisibleSourcesWithFinalAnswer`, `BuildSourcesPayload` n'y sont plus.
- `git diff --check`
  - OK sans erreur bloquante.
  - Avertissements CRLF/LF encore presents sur plusieurs fichiers du worktree.

Statut apres cette coupe :

- Le chemin source-backed actif est plus lisible sur la partie source cards.
- Le runtime garde les verifications mecaniques et la production du payload UI sans remettre le code en position de decideur semantique.
- `State.cs` reste encore trop gros ; prochaine cible structurelle logique :
  - extraire les helpers de retrieval planning / evidence exploration ;
  - ou extraire les analyses de support structured planning ;
  - puis relancer la validation longue `StructuredPlanningCoverage` de maniere plus fine.

## Mise a jour - 2026-07-09 - extraction planning draft / evidence sufficiency

Nouvelle coupe realisee :

- Le build casse par l'extraction precedente a ete repare.
- Le bloc planning/draft actif retire trop largement de `State.cs` a ete restaure dans une partial specialisee :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningDraft.cs`.
- Le bloc d'analyse de suffisance/couverture source-backed a ete extrait de `State.cs` vers :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedEvidenceSufficiency.cs`.
- Les partials d'exploration/retrieval source-backed deja extraites sont maintenant verrouillees par test d'architecture.
- `ToolAgentOrchestrator.State.cs` est descendu a environ 26 434 lignes.

Point important :

- La restauration initiale depuis `HEAD` avait ramene une version trop ancienne du bloc planning/draft.
- Une ref Codex locale sous forme de tree a ete utilisee pour recuperer une version plus recente du bloc `BuildSourceBackedPlanningDraft(...)` / ranking / slot-aware grid.
- Les gates writer/couverture plus recentes ont ete conservees dans la partial planning.

Validation ajoutee :

- Nouveau test :
  - `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump`.
- Il verifie :
  - la presence des partials planning/evidence/retrieval ;
  - une limite de taille par partial ;
  - l'absence dans `State.cs` des signatures principales extraites.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche active `legacy_.*disabled`, `SAAIA_LEGACY_RAG`, `#if SAAIA_LEGACY`, `TryExpandSourceBackedEvidenceRetrievalAsync(` hors `OLD` :
  - aucune occurrence.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Validation non validee :

- `StructuredPlanningCoverage` n'est pas vert.
- Une tentative longue a depasse 5 minutes puis a ete arretee proprement.
- Un sous-ensemble rapide de 5 tests a echoue :
  - `Structured_meal_planning_navigation_followup_skips_mechanical_noise_and_keeps_candidate_anchors` ;
  - `Structured_planning_prefers_quoted_item_title_over_reference_source_label` ;
  - `Structured_planning_card_candidate_does_not_suppress_distinct_page_local_fallback` ;
  - `Structured_planning_partial_candidate_set_exposes_no_decorative_sources` ;
  - `Structured_planning_accepts_visible_source_citations_even_when_candidate_filter_rejects`.

Statut apres cette coupe :

- La base compile.
- Les validations du pipeline source-backed canonique et de l'observabilite planner sont vertes.
- Le code actif hors `OLD` ne contient pas de hardcoding cuisine detecte par scan.
- `State.cs` et `ToolAgentOrchestrator.cs` restent beaucoup trop gros.
- Prochaine cible recommandee :
  - sortir les fonctions de rejet/diagnostic de candidats encore restantes dans `State.cs` ;
  - puis isoler ou remplacer les tests historiques structured planning qui encodent encore des attentes cuisine, en gardant leur valeur de regression dans des tests explicitement legacy ou fixtures domaine.

## Mise a jour - 2026-07-09 - extraction diagnostics candidats planning

Nouvelle coupe realisee :

- La responsabilite "diagnostic / rejet / traces de candidats structured planning" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningCandidateDiagnostics.cs`.
- Le fichier reste court :
  - environ 203 lignes.
- `ToolAgentOrchestrator.State.cs` est passe d'environ 26 434 lignes a environ 26 241 lignes.

Ce qui a ete deplace :

- Explication des raisons de rejet de candidats source-backed.
- Formatage des booleens et valeurs de trace planning.
- Formatage des compteurs de pools structured planning.
- Echantillonnage lisible des candidats d'options source-backed.
- Resolution de seuils mecaniques de candidats.
- Helpers generiques de couverture d'ancres planning.

Point important :

- Cette extraction ne remet pas le code en position de juge semantique.
- Le code extrait produit des diagnostics, applique des seuils mecaniques et expose la tracabilite.
- La decision sur la pertinence semantique d'une source doit rester dans le LLM judge / planner.

Validation ajoutee :

- Le test d'architecture `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant aussi :
  - la presence de `ToolAgentOrchestrator.SourceBackedPlanningCandidateDiagnostics.cs` ;
  - sa taille maximale ;
  - l'absence dans `State.cs` des signatures principales extraites.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- La base reste compilable.
- Les tests ciblant le pipeline source-backed canonique restent verts.
- Les diagnostics de candidats sont maintenant separes du gros dump `State.cs`.
- `State.cs` et `ToolAgentOrchestrator.cs` restent beaucoup trop gros.
- Prochaine cible structurelle recommandee :
  - identifier la prochaine responsabilite encore enfouie dans `State.cs` ou `ToolAgentOrchestrator.cs` ;
  - prioriser les blocs qui appartiennent clairement au flux canonique (`planner`, `retrieval`, `EvidenceBundle`, `judge`, `writer`, `verifier`, `repair`, `trace`, `UI source payload`) ;
  - eviter les changements comportementaux non necessaires tant que l'objectif est la remise en ordre de l'architecture.

## Mise a jour - 2026-07-09 - extraction retrieval planning queries

Nouvelle coupe realisee :

- La responsabilite "construction des requetes de retrieval planning/source-backed" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedRetrievalPlanningQueries.cs`.
- Le fichier reste sous le seuil d'architecture :
  - environ 852 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu environ 842 lignes sur cette coupe.

Ce qui a ete deplace :

- `BuildPlanningRetrievalQueries`.
- `BuildPlanningExplorationRetrievalQueries`.
- `BuildStructuredPlanningCandidateDiscoveryRetrievalQueries`.
- Requetes d'inventaire/balancing structured planning.
- Extraction et normalisation des termes de slots planning.
- `BuildSourceBackedActionRetrievalQueries`.
- `BuildSourceBackedEvidenceExpansionRetrievalQueries`.

Point important :

- Le deplacement garde la logique existante intacte.
- Le code reste dans son role de construction de requetes/outils.
- Cette coupe ne transforme pas le code en juge semantique de pertinence des sources.

Validation ajoutee :

- Le test d'architecture `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedRetrievalPlanningQueries.cs` ;
  - une limite de 1000 lignes ;
  - l'absence dans `State.cs` des signatures principales de retrieval planning extraites.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- La partie retrieval planning source-backed est maintenant isolee.
- Le pipeline reste generique et non lie a une categorie metier.
- Les validations ciblees restent vertes.
- `State.cs` et `ToolAgentOrchestrator.cs` restent encore trop gros.
- Prochaine cible structurelle logique :
  - extraire les briefs/rosters writer encore presents dans `State.cs` ;
  - ou extraire les classifications de requetes documentaires/planning si elles forment un bloc assez coherent.

## Mise a jour - 2026-07-09 - extraction classification requetes source-backed

Nouvelle coupe realisee :

- La responsabilite "classification des demandes documentaires/source-backed" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedRequestClassification.cs`.
- Le fichier reste court :
  - environ 516 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu environ 507 lignes sur cette coupe.

Ce qui a ete deplace :

- Detection des demandes de planning source-backed.
- Detection des demandes documentaires de planning.
- Detection des demandes de checklist / verification.
- Detection des demandes d'option source-backed.
- Detection des demandes de vue d'ensemble/orientation documentaire.
- Detection des suivis deictiques source-backed non resolus.
- Detection des demandes documentaires larges ou de contenu documentaire.
- Clarifications associees aux scopes de verification vagues.

Point important :

- Cette classification reste generique.
- Aucun comportement metier ou categorie de documents n'a ete ajoute.
- Le code classe la forme de la demande pour router les outils ; il ne decide toujours pas si une source repond semantiquement a la question.

Validation ajoutee :

- Le test d'architecture `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedRequestClassification.cs` ;
  - une limite de 700 lignes ;
  - l'absence dans `State.cs` des signatures principales extraites.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- Les classifications d'intention documentaire/source-backed sont separees du dump `State.cs`.
- La lisibilite du flux planner/retrieval progresse.
- `State.cs` reste encore trop gros, environ 28 687 lignes apres cette coupe.
- Prochaine cible structurelle logique :
  - extraire les briefs/rosters writer source-backed ;
  - ou isoler les verifications/reparations de reponse source-backed encore enfouies dans `State.cs`.

## Mise a jour - 2026-07-09 - extraction writer guidance source-backed

Nouvelle coupe realisee :

- La responsabilite "guidance writer source-backed" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedWriterGuidance.cs`.
- Le fichier reste court :
  - environ 490 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu environ 478 lignes sur cette coupe.

Ce qui a ete deplace :

- Construction des consignes de forme de reponse pour le writer.
- Hints de couverture source-backed pour le writer.
- Brief d'ecriture source-backed.
- Research map privee pour le writer.
- Guidance de structure demandee par l'utilisateur.
- Detection des axes explicites : jours, slots/periodes, alternatives.
- Labels localises des jours.

Point important :

- Cette coupe isole les consignes d'ecriture donnees au LLM writer.
- Elle ne change pas la logique semantique : le LLM garde la responsabilite d'organiser et juger la reponse finale.
- Le code reste charge de produire un contexte de travail et des contraintes de source contractuelles.

Validation ajoutee :

- Le test d'architecture `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedWriterGuidance.cs` ;
  - une limite de 700 lignes ;
  - l'absence dans `State.cs` des signatures principales extraites.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- La guidance writer est separee du dump `State.cs`.
- Les traces/consignes restent orientees pipeline source-backed canonique.
- `State.cs` reste encore trop gros, environ 28 208 lignes apres cette coupe.
- Prochaine cible structurelle logique :
  - extraire l'inventaire writer / rosters / mapping citations ;
  - puis isoler les verifications et reparations de reponse source-backed.

## Mise a jour - 2026-07-09 - extraction inventaire evidence writer

Nouvelle coupe realisee :

- La responsabilite "inventaire evidence writer / rosters EVIDENCE_ITEM" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedWriterEvidenceInventory.cs`.
- Le fichier reste sous le seuil d'architecture :
  - environ 892 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu environ 879 lignes sur cette coupe.

Ce qui a ete deplace :

- Construction de l'inventaire prive donne au writer.
- Limitation du nombre de lignes / caracteres de l'inventaire evidence.
- Construction des rosters de sources planning.
- Construction des rosters visibles structured planning.
- Ajout des lignes `EVIDENCE_ITEM`.
- Evasion des attributs de roster.
- Classification diagnostique des lignes de roster.
- Metadata de route/fit des candidats et pages sources.

Point important :

- L'inventaire writer reste un support de travail pour le LLM.
- Le mapping/replacement des citations et les reparations restent separes pour une prochaine coupe.
- Cette extraction ne change pas le comportement : elle range la responsabilite d'inventaire dans une partial dediee.

Validation ajoutee :

- Le test d'architecture `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedWriterEvidenceInventory.cs` ;
  - une limite de 1000 lignes ;
  - l'absence dans `State.cs` des signatures principales extraites.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- L'inventaire evidence writer est separe du dump `State.cs`.
- `State.cs` reste encore trop gros, environ 27 328 lignes apres cette coupe.
- Prochaine cible structurelle logique :
  - extraire le mapping/replacement des citations writer ;
  - puis isoler les verifications/reparations de reponse source-backed.

## Mise a jour - 2026-07-09 - extraction citations/cues evidence writer

Nouvelle coupe realisee :

- La responsabilite "citations/cues evidence writer" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedWriterEvidenceCitations.cs`.
- Le fichier reste court :
  - environ 533 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu environ 521 lignes sur cette coupe.

Ce qui a ete deplace :

- Remplacement des references `EVIDENCE_ITEM` par des citations visibles.
- Normalisation de l'ordre des citations structured planning.
- Nettoyage des citations leading/tail.
- Suppression des IDs evidence non resolus.
- Construction de la map evidence-id -> citation.
- Construction du pool de sources depuis le roster writer.
- Parsing des attributs de roster.
- Construction de citations inline.
- Construction des cues evidence/support pour le writer.

Point important :

- Le mapping citation reste mecanique : il relie des IDs et des sources visibles.
- Le jugement semantique de pertinence reste cote LLM writer/judge.
- Les guards de qualite/reparation de reponse restent dans `State.cs` pour une prochaine extraction dediee.

Validation ajoutee :

- Le test d'architecture `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedWriterEvidenceCitations.cs` ;
  - une limite de 700 lignes ;
  - l'absence dans `State.cs` des signatures principales extraites.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- Les citations/cues evidence writer sont separees du dump `State.cs`.
- `State.cs` reste encore trop gros, environ 26 806 lignes apres cette coupe.
- Prochaine cible structurelle logique :
  - extraire les guards/verifications/reparations de reponse source-backed ;
  - puis continuer a reduire les blocs de selection/filtrage candidats encore massifs.

## Mise a jour - 2026-07-09 - extraction writer answer quality guards

Nouvelle coupe realisee :

- La responsabilite "guards de qualite de reponse writer" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedWriterAnswerQuality.cs`.
- Le fichier reste court :
  - environ 347 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu environ 338 lignes sur cette coupe.

Ce qui a ete deplace :

- Detection de fuite de consignes internes writer.
- Resolution de la forme de reponse demandee.
- Detection des shapes broad synthesis.
- Detection des dumps d'extraits bruts.
- Detection des reponses planning trop remplies ou trop pauvres.
- Detection des tableaux Markdown pipe a eviter dans le client.
- Detection des reponses planning qui sous-utilisent les candidats sources.
- Matching simple entre candidats sources et texte de reponse.

Point important :

- Ce bloc reste un guard de qualite de sortie.
- Il ne juge pas qu'une source repond semantiquement a la demande utilisateur.
- Il detecte des problemes mecaniques de forme, de fuite de contexte prive, de sur-remplissage, de sous-utilisation et de presentation.

Validation ajoutee :

- Le test d'architecture `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedWriterAnswerQuality.cs` ;
  - une limite de 500 lignes ;
  - l'absence dans `State.cs` des signatures principales extraites.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- Les guards de qualite de sortie writer sont separes du dump `State.cs`.
- `State.cs` reste encore trop gros, environ 26 467 lignes apres cette coupe.
- Prochaine cible structurelle logique :
  - extraire le verifier de support planning (`PlanningAnswerSupportAnalysis`, `AnalyzeSourceBackedPlanningAnswerSupport`, `ShouldRejectUnsupportedPlanningAnswerForFinal`) ;
  - ou extraire les reparations structured planning autour de `BuildStructuredPlanningRepairFeedback`.

## Mise a jour - 2026-07-09 - extraction facade verifier planning answer

Nouvelle coupe realisee :

- La responsabilite "facade verifier de reponse planning source-backed" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningAnswerVerifier.cs`.
- Le fichier reste court :
  - environ 319 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu environ 309 lignes sur cette coupe.

Ce qui a ete deplace :

- Detection d'une reponse planning source-backed non supportee.
- Recuperation des sources d'items planning supportes.
- Verification des sources structured planning visibles/citees.
- Calcul de la diversite des sources visibles citees.
- Comptage des items concrets visibles dans une reponse structured planning.
- Verification de structure minimale visible : jours, slots, citations et placeholders.

Point important :

- Cette coupe separe la facade de verification appelee par le runtime.
- L'analyse profonde (`AnalyzeSourceBackedPlanningAnswerSupport`, `PlanningAnswerSupportAnalysis`, matching candidat/preuve) reste encore dans `State.cs` pour une extraction dediee.
- Le code reste sur une verification mecanique de contrat/source/citation/structure ; il ne devient pas juge semantique de pertinence des sources.

Validation ajoutee :

- Le test d'architecture `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedPlanningAnswerVerifier.cs` ;
  - une limite de 500 lignes ;
  - l'absence dans `State.cs` des signatures principales extraites.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- La facade verifier planning answer est separee du dump `State.cs`.
- `State.cs` reste encore trop gros, environ 26 157 lignes apres cette coupe.
- Prochaine cible structurelle logique :
  - extraire le moteur d'analyse de support planning ;
  - ou extraire la boucle de repair structured planning.

## Mise a jour - 2026-07-09 - extraction repair/finalisation planning answer

Nouvelle coupe realisee :

- La responsabilite "repair/finalisation de reponse planning source-backed" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningAnswerRepair.cs`.
- Le fichier reste sous le seuil d'architecture :
  - environ 673 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu environ 662 lignes sur cette coupe.

Ce qui a ete deplace :

- Decision de retry apres echec du source guard structured planning.
- Feedback de repair structured planning.
- Pools de sources visibles et support canonique.
- Draft planning base sur inventaire de sources visibles.
- Construction de reponses planning supportees.
- Finalisation d'une reponse planning source-backed apres analyse, sources visibles et repair.
- Helpers de couverture trusted/partial draft.
- Gestion de placeholder de completion et de grille complete.

Point important :

- Cette coupe separe la boucle de repair/finalisation du verifier profond.
- Le repair reste guide par des checks mecaniques de structure/source/citation.
- Le LLM conserve la responsabilite de reecrire une reponse utile ; le code fournit le feedback et valide le contrat source-backed.

Validation ajoutee :

- Le test d'architecture `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedPlanningAnswerRepair.cs` ;
  - une limite de 800 lignes ;
  - l'absence dans `State.cs` des signatures principales extraites.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- La boucle repair/finalisation planning answer est separee du dump `State.cs`.
- `State.cs` reste encore trop gros, environ 25 494 lignes apres cette coupe.
- Prochaine cible structurelle logique :
  - extraire le moteur d'analyse de support planning (`AnalyzeSourceBackedPlanningAnswerSupport`, `PlanningAnswerSupportAnalysis`, extraction/matching des items) ;
  - puis continuer sur les blocs massifs de selection/filtrage candidats.

## Mise a jour - 2026-07-09 - extraction support analysis planning answer

Nouvelle coupe realisee :

- La responsabilite "analyse de support planning answer" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningSupportAnalysis.cs`.
- Le fichier reste court :
  - environ 550 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu environ 540 lignes sur cette coupe.

Ce qui a ete deplace :

- `ShouldRejectUnsupportedPlanningAnswerForFinal`.
- `AnalyzeSourceBackedPlanningAnswerSupport`.
- `PlanningAnswerSupportAnalysis`.
- Extraction des items concrets d'une reponse planning.
- Support des items par candidats source-backed.
- Collecte de support via sources visibles/citees.
- Ajout de candidats de support depuis les sources visibles.
- Hook test `DeriveSourcesFromSupportedPlanningAnswerItemsForTests`.

Point important :

- Cette coupe isole le coeur de l'analyse mecanique de support.
- Le code verifie qu'une reponse porte des items, sources et citations coherents avec les candidats disponibles.
- Le jugement semantique reste au LLM : le code ne decide pas si la source "repond" a la demande utilisateur, il controle le contrat de support et de traçabilite.
- Les predicats profonds de preuve locale restent encore dans `State.cs` et doivent etre extraits dans une prochaine partial dediee.

Validation ajoutee :

- Le test d'architecture `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedPlanningSupportAnalysis.cs` ;
  - une limite de 700 lignes ;
  - l'absence dans `State.cs` des signatures principales extraites.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- Le coeur d'analyse de support planning answer est separe du dump `State.cs`.
- `State.cs` reste encore trop gros, environ 24 953 lignes apres cette coupe.
- Prochaine cible structurelle logique :
  - extraire les predicats de preuve locale structured planning ;
  - puis extraire la selection/filtrage massif de candidats planning.

## Mise a jour - 2026-07-09 - extraction predicats preuve locale planning

Nouvelle coupe realisee :

- La responsabilite "preuve locale structured planning" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningLocalProof.cs`.
- Le fichier reste sous garde d'architecture :
  - 896 lignes ;
  - limite testee a 1200 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 886 lignes sur cette coupe :
  - 24 066 lignes apres extraction.

Ce qui a ete deplace :

- Detection de preuve locale concrete pour candidats structured planning.
- Detection de bruit navigation/index autour des preuves locales.
- Detection de texte fort de preuve locale structured planning.
- Verification de termes d'items planning completement supportes.
- Extraction de termes de support planning generiques.
- Caches associes aux predicats texte de preuve locale.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedPlanningLocalProof.cs` ;
  - une limite de 1200 lignes ;
  - l'absence dans `State.cs` de `HasConcreteStructuredPlanningCandidateProof` ;
  - l'absence dans `State.cs` de `LooksLikeStructuredPlanningNavigationOrIndexNoise` ;
  - l'absence dans `State.cs` de `StructuredPlanningItemTermsAreFullySupported`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Probleme nouveau identifie :

- Plusieurs fichiers actifs contiennent des sequences mojibake (`Ã©`, `ÃƒÂ©`, etc.).
- Ce n'est pas seulement un probleme d'affichage Git : le contenu decode en UTF-8 contient bien ces sequences.
- Il ne faut pas reparer cela en remplacement global aveugle, car certains regex peuvent chercher volontairement a tolerer des textes OCR deja corrompus.
- Action propre a prevoir :
  - ajouter un test d'architecture/hygiene qui distingue les messages/prompts/commentaires corrompus des tolerances regex volontaires ;
  - corriger ensuite les cas non intentionnels fichier par fichier.

Statut apres cette coupe :

- Les predicats de preuve locale planning ne sont plus enfouis dans `State.cs`.
- La structure avance vers des responsabilites separees, mais `State.cs` et `ToolAgentOrchestrator.cs` restent encore trop gros.
- Prochaine cible structurelle logique :
  - extraire la selection/filtrage massif de candidats planning ;
  - isoler ensuite les helpers generiques de normalisation/termes/support ;
  - traiter l'hygiene encodage/mojibake avec une garde de test pour eviter le bricolage.

## Mise a jour - 2026-07-09 - extraction selection candidats planning

Nouvelle coupe realisee :

- La responsabilite "selection primaire des candidats planning" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningCandidateSelection.cs`.
- Le fichier reste sous garde d'architecture :
  - 603 lignes ;
  - limite testee a 800 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 591 lignes sur cette coupe :
  - 23 474 lignes apres extraction.

Ce qui a ete deplace :

- Cache de selection candidats planning.
- `SelectSourceBackedPlanningCandidates`.
- Cle de cache de selection.
- `SelectSourceBackedPlanningCandidatesUncached`.
- Deduplication primaire des titres partiels.
- Predicats de doublons partiels/contextuels/tronques.

Probleme rencontre et resolu :

- Premier build apres extraction casse :
  - `CultureInfo` manquait dans la nouvelle partial.
- Correction :
  - ajout de `using System.Globalization;`.
- Build relance ensuite avec succes.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedPlanningCandidateSelection.cs` ;
  - une limite de 800 lignes ;
  - l'absence dans `State.cs` de `SelectSourceBackedPlanningCandidates` ;
  - l'absence dans `State.cs` de `RemoveSourceBackedPlanningPartialTitleDuplicates` ;
  - l'absence dans `State.cs` du cache `SourceBackedPlanningCandidateSelectionCacheMaxEntries`.

Commandes validees apres correction :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- L'entree principale de selection candidats planning est maintenant isolee.
- Les helpers profonds de qualification/rejet de titres restent encore nombreux dans `State.cs`.
- Prochaine cible structurelle logique :
  - extraire les predicats de qualification/rejet des titres structured planning ;
  - ou extraire les helpers de preuve content-card/page-local selon la frontiere la plus courte.

## Mise a jour - 2026-07-09 - extraction preuve candidat page-local

Nouvelle coupe realisee :

- La responsabilite "preuve candidat/page-local structured planning" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningCandidateEvidence.cs`.
- Le fichier reste sous garde d'architecture :
  - 399 lignes ;
  - limite testee a 600 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 388 lignes sur cette coupe :
  - 23 085 lignes apres extraction.

Ce qui a ete deplace :

- Matching du candidat avec le top-level dominant.
- Detection de preuve directe du candidat.
- Support par preuve primaire de page.
- Support des titres clipses.
- Construction des textes de preuve primaire/page-local/finale.
- Rejet des surfaces finales d'orientation.
- Detection de fragments de preuve finale concrets.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedPlanningCandidateEvidence.cs` ;
  - une limite de 600 lignes ;
  - l'absence dans `State.cs` de `SourceBackedPlanningCandidateMatchesDominantTopLevel` ;
  - l'absence dans `State.cs` de `BuildPrimarySourceBackedPlanningEvidenceText` ;
  - l'absence dans `State.cs` de `BuildPageLocalSourceBackedPlanningProofText`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- Les textes de preuve candidat/page-local sont separes du dump `State.cs`.
- Les prochaines zones volumineuses encore dans `State.cs` sont :
  - predicats de qualification/rejet de titres structured planning ;
  - record/options source-backed ;
  - generation de reponses option/countdown ;
  - content-card proof helpers.

## Mise a jour - 2026-07-09 - extraction eligibility candidats planning

Nouvelle coupe realisee :

- Le noyau "cles + eligibility candidats planning" a ete sorti de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningCandidateEligibility.cs`.
- Le fichier reste sous garde d'architecture :
  - 318 lignes ;
  - limite testee a 500 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 309 lignes sur cette coupe :
  - 22 775 lignes apres extraction.

Ce qui a ete deplace :

- Cle stable de candidat planning.
- Cle de lead candidat planning.
- `IsUsableSourceBackedPlanningCandidate`.
- Rejet de candidats non concrets.
- Rejet de labels d'axe planning demandes.
- Predicats single-term loose context/action fragment/tight attachment.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedPlanningCandidateEligibility.cs` ;
  - une limite de 500 lignes ;
  - l'absence dans `State.cs` de `BuildSourceBackedPlanningCandidateKey` ;
  - l'absence dans `State.cs` de `IsUsableSourceBackedPlanningCandidate` ;
  - l'absence dans `State.cs` de `SingleTermCandidateHasTightStructuredTitleAttachment`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- Le noyau de qualification primaire des candidats est separe.
- Les grosses zones restantes dans `State.cs` commencent maintenant aux helpers content-card/field-value/noisy-title.
- Prochaine cible logique :
  - extraire `ContentCardsCarryStructuredPlanningEvidenceForTitle` + les predicats de field-value dans une partial dediee, en evitant de depasser environ 1000 lignes.

## Mise a jour - 2026-07-09 - extraction field-value candidats planning

Nouvelle coupe realisee :

- La responsabilite "field-value/content-card candidate rejection" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningFieldValueCandidates.cs`.
- Le fichier reste sous garde d'architecture :
  - 1104 lignes ;
  - limite testee a 1200 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 1094 lignes sur cette coupe :
  - 21 680 lignes apres extraction.

Ce qui a ete deplace :

- Detection de preuves content-card portant un titre planning.
- Rejet des short-section candidates non attaches.
- Rejet des generic structured field labels.
- Detection/explanation des field-value candidates.
- Detection des field-value delimited/connector/bare quantity/supporting/embedded.
- Verification de titres apparaissant dans des listes/fields avant section process.
- Ancrage des titres content-card avant les champs structures.
- Detection de candidats concurrents dans le bridge.
- Detection de preuves content-card nommant un autre item.
- Normalisation cheap title evidence issue des content cards.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedPlanningFieldValueCandidates.cs` ;
  - une limite de 1200 lignes ;
  - l'absence dans `State.cs` de `ContentCardsCarryStructuredPlanningEvidenceForTitle` ;
  - l'absence dans `State.cs` de `LooksLikeStructuredPlanningFieldValueCandidate` ;
  - l'absence dans `State.cs` de `ContentCardEvidenceNamesDifferentStructuredPlanningItem`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- Le bloc field-value/content-card candidate rejection est isole.
- `State.cs` reste encore trop gros, mais il a perdu plusieurs milliers de lignes depuis le debut de la reprise de nettoyage.
- Prochaine cible logique :
  - extraire les predicats `LooksLikeReferenceAttributionSourceTitle`, `LooksLikeConcreteStructuredPlanningCandidateTitle`, `LooksLikeNoisyStructuredPlanningCandidateTitle` et les helpers de title-quality associes ;
  - ensuite envisager l'extraction des records/options source-backed et de la generation de reponse option/countdown.

## Mise a jour - 2026-07-09 - extraction title-quality candidats planning

Nouvelle coupe realisee :

- La responsabilite "title-quality structured planning" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningTitleQuality.cs`.
- Le fichier reste sous garde d'architecture :
  - 1037 lignes ;
  - limite testee a 1200 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 1027 lignes sur cette coupe :
  - 20 652 lignes apres extraction.

Ce qui a ete deplace :

- Rejet des titres d'attribution/reference.
- Rejet des listes de noms propres/personnes.
- Detection de titres concrete structured planning.
- Detection de titres noisy structured planning.
- Detection de weak anchor followup titles.
- Rejet de phrases descriptives attachees a un titre.
- Rejet de bruit inventory/action/geography/contact/address.
- Detection de titres taxonomy/inventory.
- Detection de split OCR / OCR continuation.
- Detection glossary/definition/standalone field labels.
- Detection de fragments fused structured field labels.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedPlanningTitleQuality.cs` ;
  - une limite de 1200 lignes ;
  - l'absence dans `State.cs` de `LooksLikeReferenceAttributionSourceTitle` ;
  - l'absence dans `State.cs` de `LooksLikeConcreteStructuredPlanningCandidateTitle` ;
  - l'absence dans `State.cs` de `LooksLikeNoisyStructuredPlanningCandidateTitle`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- La qualification des titres structured planning est maintenant isolee.
- `State.cs` reste encore trop gros, mais il est passe sous 21 000 lignes.
- Prochaine cible logique :
  - extraire les predicats de planning context candidates (`LooksLikePlanningFrameOrAdviceCandidate`, `LooksLikeGenericPlanningContextCandidate`, `LooksLikePageContextLabelPlanningCandidate`, etc.) ;
  - puis isoler les records/options source-backed et la generation option/countdown.

## Mise a jour - 2026-07-09 - extraction planning-context candidates

Nouvelle coupe realisee :

- La responsabilite "planning context candidates" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningContextCandidates.cs`.
- Le fichier reste sous garde d'architecture :
  - 352 lignes ;
  - limite testee a 500 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 342 lignes sur cette coupe :
  - 20 309 lignes apres extraction.

Ce qui a ete deplace :

- `LooksLikePlanningFrameOrAdviceCandidate`.
- Rejet des leading connector structured planning fragments.
- Rejet des generic structured inventory titles.
- Rejet des generic inventory surface candidates.
- Detection taxonomy path without concrete structured item body.
- Enumeration des raw title surfaces candidat planning.
- `LooksLikeGenericPlanningContextCandidate`.
- `LooksLikePageContextLabelPlanningCandidate`.
- `LooksLikeGenericCadenceOrTimingStatement`.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedPlanningContextCandidates.cs` ;
  - une limite de 500 lignes ;
  - l'absence dans `State.cs` de `LooksLikePlanningFrameOrAdviceCandidate` ;
  - l'absence dans `State.cs` de `LooksLikeGenericPlanningContextCandidate` ;
  - l'absence dans `State.cs` de `LooksLikePageContextLabelPlanningCandidate`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- Les predicats contextuels de rejet planning sont isoles.
- `State.cs` est proche du seuil symbolique de 20 000 lignes, mais reste encore bien trop volumineux.
- Prochaine cible logique :
  - extraire le bloc `ResolveSourceBackedPlanningTargetItemCount` + records planning/options/countdown dans une partial de modeles/shape ;
  - puis separer la generation countdown/option answer.

## Mise a jour - 2026-07-09 - extraction modeles planning source-backed

Nouvelle coupe realisee :

- Les modeles/shape planning source-backed ont ete sortis de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedPlanningModels.cs`.
- Le fichier reste sous garde d'architecture :
  - 62 lignes ;
  - limite testee a 200 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 51 lignes sur cette coupe :
  - 20 257 lignes apres extraction.

Ce qui a ete deplace :

- `ResolveSourceBackedPlanningTargetItemCount`.
- `SourceBackedOptionCandidate`.
- `SourceBackedPlanningDraft`.
- `SourceBackedOptionAnswerSelection`.
- `SourceBackedCountdownCandidate`.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedPlanningModels.cs` ;
  - une limite de 200 lignes ;
  - l'absence dans `State.cs` de `ResolveSourceBackedPlanningTargetItemCount` ;
  - l'absence dans `State.cs` de `SourceBackedPlanningDraft` ;
  - l'absence dans `State.cs` de `SourceBackedOptionAnswerSelection`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- Les records planning/options/countdown ne sont plus enfouis dans `State.cs`.
- `State.cs` reste juste au-dessus de 20 000 lignes.
- Prochaine cible logique :
  - extraire la generation countdown (`LooksLikeSourceBackedCountdownPlanningRequest`, `BuildSourceBackedCountdownPlanningAnswer`, `SelectSourceBackedCountdownPlanningCandidates`, time parsing/formatting) ;
  - puis extraire la generation option answer.

## Mise a jour - 2026-07-09 - extraction countdown planning

Nouvelle coupe realisee :

- La responsabilite "countdown planning source-backed" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedCountdownPlanning.cs`.
- Le fichier reste sous garde d'architecture :
  - 246 lignes ;
  - limite testee a 400 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 233 lignes sur cette coupe :
  - 20 023 lignes apres extraction.

Ce qui a ete deplace :

- `LooksLikeSourceBackedCountdownPlanningRequest`.
- `BuildSourceBackedCountdownPlanningAnswer`.
- `SelectSourceBackedCountdownPlanningCandidates`.
- `BuildCountdownFallbackTitle`.
- `TryExtractRequestedClockTime`.
- `FormatClockTime`.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedCountdownPlanning.cs` ;
  - une limite de 400 lignes ;
  - l'absence dans `State.cs` de `LooksLikeSourceBackedCountdownPlanningRequest` ;
  - l'absence dans `State.cs` de `BuildSourceBackedCountdownPlanningAnswer` ;
  - l'absence dans `State.cs` de `TryExtractRequestedClockTime`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette coupe :

- La generation countdown est separee du dump `State.cs`.
- `State.cs` est a 20 023 lignes, donc juste au-dessus du seuil symbolique de 20 000 lignes.
- Prochaine cible logique :
  - extraire la generation option answer (`BuildSourceBackedOptionAnswer` et helpers immediats) dans une partial dediee.

## Mise a jour - 2026-07-09 - extraction option answer

Nouvelle coupe realisee :

- La responsabilite "source-backed option answer" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedOptionAnswer.cs`.
- Le fichier reste sous garde d'architecture :
  - 263 lignes ;
  - limite testee a 400 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 251 lignes sur cette coupe :
  - 19 771 lignes apres extraction.

Ce qui a ete deplace :

- `BuildSourceBackedOptionAnswer`.
- `ShouldRenderRawSourceBackedOptionEvidence`.
- `SelectSourceBackedOptionAnswerCandidates`.
- `LooksLikeTotalDurationConstraintRequest`.
- `SelectSourceBackedCombinedDurationSet`.

Important :

- La selection brute des candidats (`SelectSourceBackedOptionCandidates`) reste encore dans `State.cs`.
- La coupe a volontairement separe la generation de reponse option de la construction brute des candidats pour eviter une nouvelle partial trop grosse.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedOptionAnswer.cs` ;
  - une limite de 400 lignes ;
  - l'absence dans `State.cs` de `BuildSourceBackedOptionAnswer` ;
  - l'absence dans `State.cs` de `SelectSourceBackedOptionAnswerCandidates` ;
  - l'absence dans `State.cs` de `SelectSourceBackedCombinedDurationSet`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - premier lancement interrompu par timeout outil apres compilation partielle, sans `dotnet.exe` restant ;
  - relance OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Controle processus :
  - aucun `dotnet.exe` restant actif apres validation.

Statut apres cette coupe :

- `State.cs` est passe sous 20 000 lignes.
- La generation option answer est separee de la selection brute des candidats.
- Prochaine cible logique :
  - extraire `SelectSourceBackedOptionCandidates` et ses helpers directs dans une partial dediee ;
  - puis continuer vers les content-card proof helpers et le gros `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-09 - extraction selection et extraction candidats option

Nouvelles coupes realisees :

- La responsabilite "selection brute des candidats option source-backed" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- La responsabilite "extraction des candidats option depuis un hit/content-card" a aussi ete separee.
- Nouveaux fichiers actifs :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedOptionCandidateSelection.cs`.
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedOptionCandidateExtraction.cs`.
- Les fichiers restent sous garde d'architecture :
  - `SourceBackedOptionCandidateSelection.cs` : 348 lignes, limite testee a 500 lignes.
  - `SourceBackedOptionCandidateExtraction.cs` : 424 lignes, limite testee a 600 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 751 lignes sur ces deux coupes :
  - 19 018 lignes apres extraction.

Ce qui a ete deplace dans `SourceBackedOptionCandidateSelection.cs` :

- `SelectSourceBackedOptionCandidates`.
- Extraction des anchor terms objet option.
- Suppression des anchor terms generiques structured planning.
- Construction du caveat pairing lead.
- Coordination des filtres : source hits, raw candidates, core filter, exclusion, kind filters, anchor filters, named entity filters.

Ce qui a ete deplace dans `SourceBackedOptionCandidateExtraction.cs` :

- `BuildSourceBackedOptionCandidatesFromHit`.
- Logs de candidats content-card.
- Boost strict structured planning.
- Extraction de titres stricts source-backed.
- Extraction de titres page-local structured planning.
- Construction d'un hit scoped a une content-card.

Important :

- Les helpers profonds de preuve content-card restent encore dans `State.cs`.
- La coupe est volontairement separee en deux fichiers pour eviter une partial geante.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedOptionCandidateSelection.cs` ;
  - la presence de `ToolAgentOrchestrator.SourceBackedOptionCandidateExtraction.cs` ;
  - les limites respectives de 500 et 600 lignes ;
  - l'absence dans `State.cs` de `SelectSourceBackedOptionCandidates` ;
  - l'absence dans `State.cs` de `ExtractSourceBackedOptionObjectAnchorTerms` ;
  - l'absence dans `State.cs` de `BuildSourceBackedOptionCandidatesFromHit` ;
  - l'absence dans `State.cs` de `BuildSourceBackedCardScopedHit`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Controle processus :
  - aucun `dotnet.exe` restant actif apres validation.

Statut apres cette coupe :

- La selection brute et l'extraction de candidats option ne sont plus dans `State.cs`.
- `State.cs` descend a 19 018 lignes.
- Prochaine cible logique :
  - extraire les helpers de preuve content-card/page-local restants ;
  - puis attaquer les helpers option-kind/title-cleaning ou le gros `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-09 - extraction content-card proof et evidence

Nouvelles coupes realisees :

- La responsabilite "content-card/page-local proof" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- La responsabilite "content-card evidence/snippets" a aussi ete separee.
- Nouveaux fichiers actifs :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedContentCardProof.cs`.
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedContentCardEvidence.cs`.
- Les fichiers restent sous garde d'architecture :
  - `SourceBackedContentCardProof.cs` : 486 lignes, limite testee a 700 lignes.
  - `SourceBackedContentCardEvidence.cs` : 765 lignes, limite testee a 900 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 1229 lignes sur ces deux coupes :
  - 17 787 lignes apres extraction.

Ce qui a ete deplace dans `SourceBackedContentCardProof.cs` :

- Construction de la fenetre de preuve page-local structured planning.
- Detection des ancres de page explicites content-card.
- Preuve page-local content-card.
- Preuve content-card ancree a une page.
- Ancrage d'un titre structured planning par body/bridge content-card.
- Extraction/compte des bridge terms structured planning.
- Preuve body structured planning ancree a la page.

Ce qui a ete deplace dans `SourceBackedContentCardEvidence.cs` :

- Detection d'un titre content-card ressemblant a une field-value dans la page primaire.
- Bridge de titres clipses.
- Preuve self-contained structured planning card.
- Detection de navigation-only evidence.
- Detection de structured body/card proof cues.
- Verification de concrete content-card evidence for title.
- Support des body terms par la page primaire.
- Ancrage source text dans page primaire.
- Enumeration des textes source evidence.
- Construction des snippets context/evidence/strict evidence content-card.

Probleme rencontre et resolu :

- Premier build apres extraction casse :
  - `CultureInfo` manquait dans `SourceBackedContentCardEvidence.cs`.
- Correction :
  - ajout de `using System.Globalization;`.
- Build relance ensuite avec succes.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedContentCardProof.cs` ;
  - la presence de `ToolAgentOrchestrator.SourceBackedContentCardEvidence.cs` ;
  - les limites respectives de 700 et 900 lignes ;
  - l'absence dans `State.cs` de `BuildSourceBackedPageLocalStructuredPlanningEvidenceWindow` ;
  - l'absence dans `State.cs` de `ContentCardHasPageLocalStructuredPlanningProof` ;
  - l'absence dans `State.cs` de `ContentCardTitleLooksLikeStructuredFieldValueInPrimaryPage` ;
  - l'absence dans `State.cs` de `ContentCardSourceTextIsAnchoredInPrimaryPageText` ;
  - l'absence dans `State.cs` de `BuildSourceBackedCardEvidenceSnippet`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - premier build apres extraction : erreur `CultureInfo` manquant ;
  - apres correction : OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Controle processus :
  - aucun `dotnet.exe` restant actif apres validation.

Statut apres cette coupe :

- Les helpers de preuve content-card/page-local sont separes.
- `State.cs` descend a 17 787 lignes.
- Prochaine cible logique :
  - extraire les helpers option-kind (`ApplySoftChoiceOptionKindScore`, pairing/soft-choice kind terms, contradictions) ;
  - puis attaquer le gros bloc title-cleaning ou le fichier `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-09 - extraction option-kind

Nouvelle coupe realisee :

- La responsabilite "option-kind scoring/matching" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedOptionKind.cs`.
- Le fichier reste sous garde d'architecture :
  - 284 lignes ;
  - limite testee a 400 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 274 lignes sur cette coupe :
  - 17 512 lignes apres extraction.

Ce qui a ete deplace :

- `ApplySoftChoiceOptionKindScore`.
- Detection de composite option anchor request.
- Extraction des option-kind terms pairing.
- Extraction des option-kind terms soft-choice.
- Extraction et normalisation des generic source-backed option-kind terms.
- Matching pairing/soft-choice option-kind.
- Scoring de match soft-choice.
- Detection de contradiction entre kind demande et candidat.
- Matching entre option-kind term et texte/retrieval query.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedOptionKind.cs` ;
  - une limite de 400 lignes ;
  - l'absence dans `State.cs` de `ApplySoftChoiceOptionKindScore` ;
  - l'absence dans `State.cs` de `ExtractGenericSourceBackedOptionKindTerms` ;
  - l'absence dans `State.cs` de `OptionKindContradictsCandidate`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Controle processus :
  - aucun `dotnet.exe` restant actif apres validation.

Statut apres cette coupe :

- Les helpers option-kind sont separes.
- `State.cs` descend a 17 512 lignes.
- Prochaine cible logique :
  - extraire les helpers d'utilisabilite/low-value option candidate ;
  - ensuite attaquer le gros bloc title-cleaning ou `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-09 - extraction option candidate quality

Nouvelle coupe realisee :

- La responsabilite "option candidate quality / title extraction" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedOptionCandidateQuality.cs`.
- Le fichier reste sous garde d'architecture :
  - 364 lignes ;
  - limite testee a 500 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 353 lignes sur cette coupe :
  - 17 158 lignes apres extraction.

Ce qui a ete deplace :

- `LooksLikeUsableSourceBackedOptionCandidate`.
- `HasConcreteFinalSourceBackedEvidence`.
- `LooksLikeLowValueSourceBackedOptionCandidate`.
- Cle stable option candidate.
- Detection primary query top concrete card.
- Cache et detection de profile title.
- Matching des query anchor terms sur un hit.
- Termes de contraintes/bruit pairing.
- Fallback option title.
- Extraction des title variants content-card.
- Extraction de named entities depuis la query.
- `ExtractSourceBackedOptionTitle`.

Important :

- Le gros bloc `CleanSourceBackedOptionTitle` et les helpers de title-cleaning restent encore dans `State.cs`.
- La coupe s'arrete volontairement juste avant `SourceBackedCleanTitleCacheMaxEntries` pour garder une prochaine partial title-cleaning coherente.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedOptionCandidateQuality.cs` ;
  - une limite de 500 lignes ;
  - l'absence dans `State.cs` de `LooksLikeUsableSourceBackedOptionCandidate` ;
  - l'absence dans `State.cs` de `LooksLikeLowValueSourceBackedOptionCandidate` ;
  - l'absence dans `State.cs` de `HasSourceBackedProfileTitle` ;
  - l'absence dans `State.cs` de `ExtractSourceBackedOptionTitle`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Controle processus :
  - aucun `dotnet.exe` restant actif apres validation.

Statut apres cette coupe :

- Les helpers d'utilisabilite/low-value option candidate sont separes.
- `State.cs` descend a 17 158 lignes.
- Prochaine cible logique :
  - extraire le bloc title-cleaning (`CleanSourceBackedOptionTitle` et helpers de nettoyage de suffixes/prefixes OCR/context) ;
  - ou commencer la reduction du gros `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-09 - extraction option title-cleaning

Nouvelle coupe realisee :

- La responsabilite "source-backed option title-cleaning" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveau fichier actif :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedOptionTitleCleaning.cs`.
- Le fichier reste sous garde d'architecture :
  - 1018 lignes ;
  - limite testee a 1200 lignes.
- `ToolAgentOrchestrator.State.cs` a perdu 1008 lignes sur cette coupe :
  - 16 149 lignes apres extraction.

Ce qui a ete deplace :

- Cache `SourceBackedCleanTitleCache`.
- `CleanSourceBackedOptionTitle`.
- `CleanSourceBackedOptionTitleUncached`.
- Restauration de suffixes significatifs clipses.
- Nettoyage de contextes principaux generiques.
- Suppression de suffixes variant/all-caps/compact OCR/generic structured context.
- Nettoyage des suffixes broken principal context.
- Detection de suffixes OCR isoles.
- Nettoyage des titres composites.
- Regex de leading structured planning field labels.
- Suppression des prefixes/suffixes structured section noise.
- Suppression des prefixes OCR compacts/fused.
- Suppression des suffixes article/context structured planning.
- Suppression des prefixes low-signal field-value et connector field-value.

Important :

- Le scoring de titre (`ComputeSourceBackedOptionTitleScore`) reste encore dans `State.cs`.
- La coupe s'arrete volontairement avant le scoring pour garder `SourceBackedOptionTitleCleaning.cs` centre sur le nettoyage pur.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedOptionTitleCleaning.cs` ;
  - une limite de 1200 lignes ;
  - l'absence dans `State.cs` de `SourceBackedCleanTitleCacheMaxEntries` ;
  - l'absence dans `State.cs` de `CleanSourceBackedOptionTitle` ;
  - l'absence dans `State.cs` de `StripTrailingBrokenPrincipalContextSuffix` ;
  - l'absence dans `State.cs` de `StripLeadingStructuredPlanningFieldLabelFromTitle`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Controle processus :
  - aucun `dotnet.exe` restant actif apres validation.

Statut apres cette coupe :

- Le nettoyage de titres source-backed option est separe.
- `State.cs` descend a 16 149 lignes.
- Prochaine cible logique :
  - extraire le scoring titre/hit et les helpers duree ;
  - ou commencer la reduction du gros `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-09 - extraction option scoring et duration parsing

Nouvelle coupe realisee :

- La responsabilite "source-backed option scoring" a ete sortie de `ToolAgentOrchestrator.State.cs`.
- La responsabilite "duration parsing" utilisee par les candidats source-backed a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveaux fichiers actifs :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedOptionScoring.cs` ;
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedDurationParsing.cs`.
- Les fichiers restent sous garde d'architecture :
  - option scoring : 92 lignes, limite testee a 200 lignes ;
  - duration parsing : 180 lignes, limite testee a 300 lignes.

Ce qui a ete deplace :

- `ComputeSourceBackedOptionTitleScore`.
- `ComputeSourceBackedOptionHitScore`.
- `FormatSourceBackedOptionEvidence`.
- `TryExtractRequestedMaxMinutes`.
- `ExtractBestVisibleDurationMinutes`.
- `ExtractBestVisibleDurationMinutesFromText`.
- `ExtractLabeledVisibleDurationTotalMinutes`.
- `NormalizeDurationScanText`.
- `NormalizeStructuredScanText`.
- `NormalizeDurationLabel`.
- `LooksLikeDurationLabelNoise`.
- `ParseVisibleDurationMinutes`.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence de `ToolAgentOrchestrator.SourceBackedOptionScoring.cs` ;
  - une limite de 200 lignes ;
  - la presence de `ToolAgentOrchestrator.SourceBackedDurationParsing.cs` ;
  - une limite de 300 lignes ;
  - l'absence dans `State.cs` des definitions `ComputeSourceBackedOptionTitleScore`, `ComputeSourceBackedOptionHitScore`, `TryExtractRequestedMaxMinutes`, `ExtractBestVisibleDurationMinutes` et `ParseVisibleDurationMinutes`.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Controle processus :
  - aucun `dotnet.exe` restant actif apres validation.

Statut apres cette coupe :

- Le scoring option et le parsing de duree sont separes.
- `State.cs` descend a 13 906 lignes.
- `ToolAgentOrchestrator.cs` reste trop gros a 11 982 lignes.
- Prochaine cible logique :
  - extraire un bloc de selection/ranking de hits source-backed encore present dans `State.cs` ;
  - continuer ensuite sur les gros blocs de `ToolAgentOrchestrator.cs`, en isolant tout reliquat legacy inutile dans `OLD` si necessaire.

## Mise a jour - 2026-07-09 - extraction hit selection, hit quality et comparative selection

Nouvelle tranche realisee :

- La selection extractive source-backed a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Les filtres mecaniques de qualite/low-signal des hits source-backed ont ete sortis de `ToolAgentOrchestrator.State.cs`.
- La selection comparative documentaire source-backed a ete sortie de `ToolAgentOrchestrator.State.cs`.
- Nouveaux fichiers actifs :
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedExtractiveHitSelection.cs` ;
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedExtractiveHitQuality.cs` ;
  - `client/SAAIA.Client.WinUI/ToolAgent/ToolAgentOrchestrator.SourceBackedComparativeHitSelection.cs`.
- Les fichiers restent courts :
  - extractive hit selection : 242 lignes, limite testee a 400 lignes ;
  - extractive hit quality : 192 lignes, limite testee a 300 lignes ;
  - comparative hit selection : 213 lignes, limite testee a 300 lignes.

Ce qui a ete deplace :

- `DeriveSourcesFromMissingExactItemCloseLeads`.
- `SelectMissingExactItemCloseLeads`.
- `ComputeTypoTolerantRagHitLexicalRelevance`.
- `SelectSourceBackedExtractiveHits`.
- `LooksLikeLowSignalAppFeatureHit`.
- `LooksLikeLowSignalContentCandidateHit`.
- `LooksLikeGenericFrontMatterShape`.
- `HasRecoverableStructuredExactItemEvidence`.
- `AddComplementaryStructuredHitsForExactItem`.
- `ComputeExactItemCardCompletenessCueScore`.
- `SelectComparativeDocumentaryHits`.
- `ComparativeScoredHit`.
- `ExtractComparativeEntityAnchorTerms`.
- `ComputeComparativeDocumentaryEvidenceScore`.

Interpretation architecturale :

- Ces blocs restent du cote "preparation mecanique des preuves" : tri, qualite minimale, deduplication implicite, couverture documentaire comparative.
- Ils ne doivent pas devenir le juge semantique final.
- La decision "est-ce que la source repond vraiment a la question ?" doit rester dans le chemin LLM evidence judge / writer, puis source verifier mecanique.

Validation ajoutee :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verrouille maintenant :
  - la presence des trois nouveaux partials ;
  - leurs limites de taille ;
  - l'absence dans `State.cs` des signatures principales de selection extractive, qualite et comparative.

Commandes validees :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette tranche :

- `State.cs` descend a 13 282 lignes.
- `ToolAgentOrchestrator.cs` reste a 11 982 lignes.
- Prochaine cible logique :
  - extraire le bloc quantity-scaling/adaptation/ranking encore dans `State.cs` ;
  - ou commencer la reduction du gros `ToolAgentOrchestrator.cs` lorsque les blocs source-backed restants sont mieux isoles.

## Mise a jour - 2026-07-09 - suppression d'un fallback writer determinant concurrent

Correction realisee :

- Les chemins timeout/overflow du writer LLM dans `ToolAgentOrchestrator.cs` ne retombent plus sur `BuildSourceBackedPlanningOrExtractiveAnswer`.
- Avant correction, ces chemins pouvaient rediger une reponse source-backed via un builder deterministe concurrent lorsque le writer LLM expirait ou debordait le contexte.
- Apres correction, ces chemins utilisent une reponse sure :
  - `BuildSourceBackedSafeFallbackAnswer(..., shouldAvoidRaw: true)` ;
  - puis, si necessaire, `BuildBroadEvidenceStillInsufficientAnswer`.
- Cela rapproche le flux de la cible :
  - le LLM reste le redacteur/judge semantique ;
  - le code ne fabrique pas une synthese finale source-backed a sa place en cas de timeout/overflow ;
  - le code peut seulement exposer une insuffisance ou une fallback sure.

Traces ajoutees :

- `writer.llm.timeout.terminal_fallback`
- `writer.llm.overflow.terminal_fallback`

Garde-fou ajoute :

- `SourceBackedRagArchitectureTests.Legacy_source_backed_runtime_branches_are_removed_from_active_orchestrator` verifie maintenant que `ToolAgentOrchestrator.cs` ne contient plus `BuildSourceBackedPlanningOrExtractiveAnswer(`.
- `SourceBackedRagArchitectureTests.Canonical_source_backed_skips_and_repairs_remain_visible_after_legacy_branch_removal` verifie la presence des deux nouvelles traces terminales.
- Le marqueur historique `writer_context_overflow_deterministic_fallback` est explicitement interdit dans l'orchestrateur actif.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres correction :

- L'orchestrateur actif ne contient plus d'appel direct a `BuildSourceBackedPlanningOrExtractiveAnswer`.
- Il reste a auditer les usages residuels de ce builder dans `State.cs` et `TestHooks.cs` pour determiner s'ils sont encore accessibles par un flux actif ou seulement par des tests/outils de diagnostic.

## Mise a jour - 2026-07-09 - source-policy guard sans synthese deterministe

Correction realisee :

- Le dernier appel runtime residuel a `BuildSourceBackedPlanningOrExtractiveAnswer` etait dans `TryBuildSourcePolicyGuardAnswer`.
- Ce chemin est utilise par le bypass pre-writer `writer_bypass_source_policy`.
- Avant correction, une demande du type "ignore les sources / invente" pouvait declencher :
  - un refus de principe ;
  - puis une reponse documentaire reconstruite par le code deterministe.
- Apres correction, le guard source-policy produit uniquement :
  - le refus de principe ;
  - une courte liste mecanique des sources disponibles, via `BuildSourcePolicyGuardSourceList`.
- Le code ne redige plus une synthese documentaire dans ce guard.

Details :

- Nouveau helper :
  - `BuildSourcePolicyGuardSourceList`.
- Nouveau helper :
  - `FormatSourcePolicyGuardSourceRef`.
- Correction opportuniste d'une chaine francaise mojibake dans `BuildDocumentInstructionPolicyAnswer` :
  - `donnÃ©e du corpus` redevient `donnée du corpus`.

Garde-fou ajoute :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` verifie maintenant que `State.cs` ne contient qu'une seule occurrence de `BuildSourceBackedPlanningOrExtractiveAnswer(` :
  - sa definition ;
  - aucun appel runtime residuel dans `State.cs`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 error.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres correction :

- `BuildSourceBackedPlanningOrExtractiveAnswer` n'est plus appele par le runtime actif inspecte.
- Il reste :
  - sa definition dans `State.cs` ;
  - un wrapper `BuildSourceBackedPlanningOrExtractiveAnswerForTests` dans `TestHooks.cs` ;
  - des tests historiques qui couvrent encore ce comportement.
- Prochaine decision structurelle :
  - soit archiver progressivement ce builder et ses tests historiques ;
  - soit le conserver temporairement comme reference test-only jusqu'a migration complete vers le pipeline canonique `EvidenceBundle`.

## Mise a jour - 2026-07-09 - archivage du builder finalizer historique et extraction source-policy

Corrections realisees :

- `BuildSourceBackedPlanningOrExtractiveAnswer` a ete retire du code actif et archive en reference dans :
  - `client\SAAIA.Client.WinUI\ToolAgent\OLD\ToolAgentOrchestrator.LegacyPlanningOrExtractiveAnswer.20260709.cs`.
- Le wrapper test-only `BuildSourceBackedPlanningOrExtractiveAnswerForTests` a ete retire de :
  - `client\SAAIA.Client.WinUI\ToolAgent\ToolAgentOrchestrator.TestHooks.cs`.
- Les tests qui appelaient encore ce helper historique ont ete rediriges vers le fallback sur actuel :
  - `BuildSourceBackedSafeFallbackAnswerForTests(..., shouldAvoidRaw: true)`.
- Le test `Broad_source_backed_raw_fallback_is_replaced_by_insufficiency_before_dumping_candidates` accepte maintenant la formulation sure `n'ai pas trouv...`, sans reutiliser l'ancien builder.

Nettoyage de vocabulaire actif :

- La trace active `source_backed_legacy_expansion.skipped` a ete remplacee par :
  - `source_backed_pipeline.canonical_retrieval.owner_confirmed`.
- Les tests d'architecture protegent maintenant la nouvelle trace canonique et interdisent explicitement le retour de l'ancien nom `source_backed_legacy_expansion.skipped`.

Extraction structurelle :

- Le bloc source-policy guard a ete extrait de `ToolAgentOrchestrator.State.cs` vers :
  - `client\SAAIA.Client.WinUI\ToolAgent\ToolAgentOrchestrator.SourceBackedSourcePolicyGuards.cs`.
- Responsabilite du nouveau partial :
  - refus mecanique des demandes de bypass source ;
  - prefixe de garde source-backed ;
  - courte liste mecanique des sources disponibles ;
  - detection des demandes "ignore les sources / invente / ne cite pas" ;
  - construction de requete d'ancrage pour garder une preuve source sans rediger une synthese semantique.
- Interpretation architecturale :
  - ce bloc reste mecanique ;
  - il ne juge pas si une source repond a la question ;
  - il empeche seulement le contournement du contrat de sources.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Broad_source_backed_raw_fallback_is_replaced_by_insufficiency_before_dumping_candidates" --verbosity:normal`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence des anciens flags/branches legacy surveilles ;
  - aucune occurrence active de `source_backed_legacy_expansion`.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut apres cette tranche :

- L'ancien builder finalizer `BuildSourceBackedPlanningOrExtractiveAnswer` est hors chemin actif.
- Le guard source-policy est isole dans un fichier specialise.
- Le vocabulaire de trace actif ne porte plus le residu `legacy_expansion`.
- Les deux plus gros ecarts restants sont toujours structurels :
  - `ToolAgentOrchestrator.State.cs` reste trop volumineux ;
  - `ToolAgentOrchestrator.cs` reste trop volumineux.
- Prochaine cible logique :
  - extraire les builders/fallbacks source-backed restants de `State.cs` ;
  - puis reduire le flux principal de `ToolAgentOrchestrator.cs` en sous-etapes orchestrateur plus courtes.

## Mise a jour - 2026-07-09 - extraction des fallback answers source-backed

Extraction realisee :

- Le bloc de construction des reponses mecaniques de fallback/insuffisance a ete extrait de `ToolAgentOrchestrator.State.cs` vers :
  - `client\SAAIA.Client.WinUI\ToolAgent\ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs`.
- Responsabilites du nouveau partial :
  - `BuildCategoryOverviewAnswer` ;
  - `BuildRagEvidenceFallbackAnswer` ;
  - `BuildBroadEvidenceStillInsufficientAnswer` ;
  - guards avant writer pour insuffisance de sources ;
  - fallback de candidats lisibles ;
  - reponses partielles structurees quand elles restent strictement source-backed ;
  - logique de clarification backend quand le corpus ne prouve pas assez.

Interpretation architecturale :

- Ce bloc reste dans le role mecanique :
  - signaler une insuffisance ;
  - eviter le dump de passages bruts ;
  - exposer des sources disponibles sans inventer une synthese semantique ;
  - proteger le contrat source-backed quand le writer/judge ne peut pas produire une reponse verifiee.
- Il ne remplace pas le LLM comme juge final de pertinence semantique.

Etat structurel :

- `ToolAgentOrchestrator.State.cs` descend a environ 13 174 lignes.
- Nouveau fichier :
  - `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs`, environ 1 428 lignes.
- Le fichier reste plus gros que l'ideal, mais la responsabilite est maintenant nommee et separee du state dump.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~Broad_source_backed_raw_fallback_is_replaced_by_insufficiency_before_dumping_candidates" --verbosity:normal`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Prochaine cible :

- Extraire ensuite les blocs parsing/normalisation des `RagHitSummary` et/ou les gros blocs de preuve visible encore dans `State.cs`.
- Ensuite attaquer le flux principal de `ToolAgentOrchestrator.cs`, qui reste trop volumineux pour la structure visee.

## Mise a jour - 2026-07-09 - extraction du parsing RagHitSummary

Extraction realisee :

- Le bloc de parsing/normalisation des hits RAG a ete extrait de `ToolAgentOrchestrator.State.cs` vers :
  - `client\SAAIA.Client.WinUI\ToolAgent\ToolAgentOrchestrator.SourceBackedRagHitSummaryParsing.cs`.
- Responsabilites du nouveau partial :
  - `BuildRagHitSummary` ;
  - lecture des pages debut/fin ;
  - extraction des content cards attachees a un hit ;
  - extraction des signaux qualite OCR/chunk/extraction ;
  - enumeration des hits RAG depuis les tool results ;
  - enumeration des hits de contexte documentaire.

Interpretation architecturale :

- Ce bloc est purement mecanique :
  - il convertit les payloads JSON RAG en objets internes exploitables ;
  - il preserve les metadonnees de provenance ;
  - il ne decide pas de la pertinence semantique finale d'une source.
- Cette separation rend plus visible la frontiere entre :
  - ingestion/parsing des preuves ;
  - jugement LLM ;
  - verification mecanique des sources.

Etat structurel :

- `ToolAgentOrchestrator.State.cs` descend a environ 12 457 lignes.
- Nouveau fichier :
  - `ToolAgentOrchestrator.SourceBackedRagHitSummaryParsing.cs`, environ 732 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Prochaine cible :

- Continuer a sortir de `State.cs` les blocs de normalisation/compaction JSON restants.
- Ensuite reduire `ToolAgentOrchestrator.cs`, qui porte encore trop de flux de controle dans un seul fichier.

## Mise a jour - 2026-07-09 - extraction normalisation et compaction JSON RAG

Extraction realisee :

- Le bloc de normalisation des requetes RAG, de normalisation des hits JSON et de compaction des payloads pour prompt a ete extrait de `ToolAgentOrchestrator.State.cs` vers :
  - `client\SAAIA.Client.WinUI\ToolAgent\ToolAgentOrchestrator.SourceBackedRagJsonNormalization.cs`.
- Responsabilites du nouveau partial :
  - `NormalizeRagQueryForRetrieval` ;
  - detection/decodage des enveloppes de recherche elargie ;
  - extraction du sujet utilisateur delimite ;
  - reparation de marqueurs d'accents ;
  - `NormalizeRagHits` ;
  - compaction des content cards, profile signals, retrieval content signals, selection hints et diagnostics qualite ;
  - helpers JSON `TryGetString`, `TryGetObject`, `TryGetArray`, `TryGetInt`, etc.

Interpretation architecturale :

- Cette responsabilite est technique et mecanique :
  - nettoyer les inputs ;
  - produire des payloads plus compacts pour le LLM ;
  - preserver la provenance et les signaux utiles ;
  - ne pas juger si une source repond au besoin final.
- Cela renforce la separation voulue :
  - code = transformation/contrats/traces ;
  - LLM = interpretation et decision semantique.

Etat structurel :

- `ToolAgentOrchestrator.State.cs` descend a environ 11 270 lignes.
- Nouveau fichier :
  - `ToolAgentOrchestrator.SourceBackedRagJsonNormalization.cs`, environ 1 195 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Statut restant :

- `State.cs` reste trop volumineux, mais il a maintenant perdu plusieurs responsabilites mecaniques majeures.
- `ToolAgentOrchestrator.cs` est maintenant le plus gros fichier actif et reste une cible prioritaire pour extraire le flux principal en sous-etapes.

## Mise a jour - 2026-07-09 - reduction du flux principal ToolAgentOrchestrator.cs

Extractions realisees :

- Le bloc de seeding/navigation source-backed a ete retire de `ToolAgentOrchestrator.cs`.
- Il est maintenant scinde en deux partials :
  - `ToolAgentOrchestrator.SourceBackedEvidenceExplorationNavigation.cs` ;
  - `ToolAgentOrchestrator.SourceBackedEvidenceExplorationNavigationSeeding.cs`.
- Le bloc LLM evidence planner a ete retire de `ToolAgentOrchestrator.cs` et deplace dans :
  - `ToolAgentOrchestrator.SourceBackedLlmEvidencePlanner.cs`.

Details structurels :

- `ToolAgentOrchestrator.SourceBackedEvidenceExplorationNavigation.cs` reste sous la limite d'architecture existante.
- Nouveau partial ajoute a la garde d'architecture :
  - `ToolAgentOrchestrator.SourceBackedEvidenceExplorationNavigationSeeding.cs`, limite 700 lignes.
- `ToolAgentOrchestrator.cs` descend a environ 10 905 lignes.
- `ToolAgentOrchestrator.State.cs` reste a environ 11 270 lignes.
- `ToolAgentOrchestrator.SourceBackedLlmEvidencePlanner.cs` monte a environ 2 145 lignes et devra probablement etre scinde plus tard en prompt building / category scope / working notes / query seeds.

Interpretation architecturale :

- Le fichier principal porte moins de details de navigation et de prompt engineering.
- Les decisions semantiques restent cote LLM planner/judge/writer.
- Le code extrait conserve son role :
  - organiser les appels ;
  - construire les prompts/outils ;
  - tracer les passes ;
  - appliquer les budgets et garde-fous mecaniques.

Probleme rencontre :

- La premiere extraction avait fait depasser la limite de ligne du partial navigation.
- Solution :
  - scission immediate dans `ToolAgentOrchestrator.SourceBackedEvidenceExplorationNavigationSeeding.cs` ;
  - ajout du fichier dans `SourceBackedRagArchitectureTests`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus de build :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Scinder `ToolAgentOrchestrator.SourceBackedLlmEvidencePlanner.cs` en responsabilites plus petites.
- Continuer a reduire `ToolAgentOrchestrator.cs` et `State.cs` sous des seuils plus raisonnables.

## Mise a jour - 2026-07-09 - scission du planner LLM evidence

Extraction realisee :

- `ToolAgentOrchestrator.SourceBackedLlmEvidencePlanner.cs` a ete scinde en partials specialises.
- Le fichier central garde maintenant l'orchestration du planner :
  - appel LLM planner ;
  - application de la decision de scope categorie ;
  - adjudication LLM de scope categorie ;
  - decisions de declenchement/defer du planner.

Nouveaux fichiers :

- `ToolAgentOrchestrator.SourceBackedLlmEvidencePlannerParsing.cs`
  - parsing JSON des passes LLM ;
  - parsing des decisions de scope categorie ;
  - helpers de scope-only pass.
- `ToolAgentOrchestrator.SourceBackedLlmEvidencePlannerPrompts.cs`
  - prompt system evidence exploration ;
  - prompt user evidence exploration.
- `ToolAgentOrchestrator.SourceBackedLlmEvidencePlannerWorkingNotes.cs`
  - memoire strategique des passes de recherche ;
  - formatage des notes pour le LLM ;
  - topic/shape keys.
- `ToolAgentOrchestrator.SourceBackedLlmEvidencePlannerCoveragePrompt.cs`
  - axes faibles/undercovered ;
  - trace de couverture planning ;
  - termes d'axes structures.
- `ToolAgentOrchestrator.SourceBackedLlmEvidencePlannerCategoryPrompt.cs`
  - prompt system/user pour adjudication categoryScope ;
  - request shape ;
  - evidence snapshot.
- `ToolAgentOrchestrator.SourceBackedLlmEvidencePlannerStructureHints.cs`
  - construction des structure hints depuis tree, summary, navigation et sources precedentes.
- `ToolAgentOrchestrator.SourceBackedLlmEvidencePlannerQuerySeeds.cs`
  - category hints ;
  - query seeds deterministes pour aider le LLM sans prendre la decision semantique ;
  - already tried queries ;
  - formatage compact de listes de prompt.

Garde-fou ajoute :

- `SourceBackedRagArchitectureTests.Extracted_source_backed_planning_partials_stay_ordered_and_out_of_state_dump` couvre maintenant tous les nouveaux partials du planner LLM avec des limites de taille.

Etat structurel :

- `ToolAgentOrchestrator.SourceBackedLlmEvidencePlanner.cs` descend a environ 373 lignes.
- Les nouveaux partials restent courts :
  - parsing environ 306 lignes ;
  - prompts environ 176 lignes ;
  - working notes environ 212 lignes ;
  - coverage prompt environ 326 lignes ;
  - category prompt environ 258 lignes ;
  - structure hints environ 392 lignes ;
  - query seeds environ 209 lignes.

Interpretation architecturale :

- Le planner LLM reste le point de decision semantique sur les prochaines recherches.
- Le code extrait ne choisit pas si une source repond a la question finale.
- Il prepare :
  - contexte compact ;
  - memoire strategique ;
  - options de tools ;
  - traces ;
  - garde-fous generiques sans hardcoding metier.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Continuer la reduction des deux plus gros fichiers actifs :
  - `ToolAgentOrchestrator.State.cs`, environ 11 270 lignes ;
  - `ToolAgentOrchestrator.cs`, environ 10 905 lignes.

## Mise a jour - 2026-07-09 - extraction quantity/adaptation/ranking hors State.cs

Extraction realisee :

- Trois blocs de finalisation source-backed generiques ont ete sortis de `ToolAgentOrchestrator.State.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.SourceBackedQuantityScaling.cs` ;
  - `ToolAgentOrchestrator.SourceBackedAdaptationAnswer.cs` ;
  - `ToolAgentOrchestrator.SourceBackedRankingAnswer.cs`.

Responsabilites extraites :

- Quantity scaling :
  - detection des demandes de scaling quantitatif ;
  - selection des sources avec quantites visibles ;
  - extraction/scaling de quantites ;
  - formatage des lignes ajustees.
- Adaptation :
  - selection de sources utiles pour une adaptation prudente ;
  - extraction des faits cibles visibles ;
  - construction de notes d'adaptation generiques.
- Ranking :
  - ranking source-backed de candidats ;
  - preservation de couverture comparative ;
  - scoring de formes d'evidence structuree ;
  - raison courte liee a chaque source.

Interpretation architecturale :

- Cette extraction ne pretend pas que ces builders sont la forme finale ideale.
- Elle rend leur role explicite et auditable.
- Elle prepare une refonte ulterieure ou ces chemins pourront etre rattaches plus clairement au flux canonique LLM judge/writer/verifier.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.State.cs` descend a environ 9 125 lignes.
- Nouveaux fichiers :
  - quantity scaling environ 963 lignes ;
  - adaptation environ 395 lignes ;
  - ranking environ 823 lignes.
- Les fichiers sont ajoutes aux gardes d'architecture avec limites de taille.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.

Prochaine cible :

- Extraire le gros bloc exact item / evidence text encore present dans `State.cs`.
- Continuer ensuite la reduction de `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-09 - extraction document version traceability

Extraction realisee :

- Le bloc de traçabilite de versions documentaires a ete extrait de `ToolAgentOrchestrator.State.cs` vers :
  - `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceability.cs`.

Responsabilites extraites :

- construction de reponse de traçabilite documentaire ;
- derivation des sources de traçabilite ;
- selection/ranking des versions documentaires ;
- detection des demandes "version principale", "archive", "historique", "remplace", "corrige" ;
- extraction de references et annees ;
- filtrage des versions recentes/historiques/archivees/contrastantes.

Interpretation architecturale :

- Le bloc est generique et documentaire, sans hardcoding metier.
- Il est maintenant auditable comme responsabilite separee.
- Il reste a terme a rattacher plus clairement au flux canonique LLM judge/writer/verifier lorsque les builders deterministes restants seront revisites.

Etat structurel :

- `ToolAgentOrchestrator.State.cs` descend a environ 8 130 lignes.
- Nouveau fichier :
  - `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceability.cs`, environ 1 008 lignes.
- Le fichier est ajoute aux gardes d'architecture.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire les blocs exact item / evidence text encore presents dans `State.cs`.
- Puis reprendre la reduction de `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-09 - extraction exact item et matching de titres

Extraction realisee :

- Quatre blocs exact-item/extractive ont ete extraits de `ToolAgentOrchestrator.State.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.SourceBackedExtractiveAnswerCore.cs` ;
  - `ToolAgentOrchestrator.SourceBackedExactItemTitleExtraction.cs` ;
  - `ToolAgentOrchestrator.SourceBackedExactItemTitleMatching.cs` ;
  - `ToolAgentOrchestrator.SourceBackedMissingExactItemAnswers.cs`.

Responsabilites extraites :

- `SourceBackedExtractiveAnswerCore` :
  - decision de routing vers extractive answer ;
  - construction de reponse extractive source-backed ;
  - orchestration des chemins exact item, comparaison, scaling, adaptation, ranking et fallback lisible.
- `SourceBackedExactItemTitleExtraction` :
  - extraction du titre demande ;
  - detection de collections/options generiques ;
  - extraction de references de fichiers PDF/documents ;
  - nettoyage de titres.
- `SourceBackedExactItemTitleMatching` :
  - matching titre demande vs hits RAG ;
  - ancres de titre fortes/faibles ;
  - detection navigation-only / route target / content card concret ;
  - extraction des titres de navigation.
- `SourceBackedMissingExactItemAnswers` :
  - messages source-backed quand un document ou item exact manque ;
  - pistes proches typo-tolerantes.

Interpretation architecturale :

- Ces blocs restent mecaniques :
  - ils identifient les titres, documents, ancres et absences ;
  - ils ne doivent pas remplacer le LLM comme juge semantique final.
- L'extraction rend visible ce qui reste a revisiter pour basculer davantage vers le flux canonique LLM judge/writer/source verifier.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.State.cs` descend a environ 6 021 lignes.
- Nouveaux fichiers :
  - extractive core environ 378 lignes ;
  - title extraction environ 896 lignes ;
  - title matching environ 658 lignes ;
  - missing exact item answers environ 229 lignes.
- Les nouveaux fichiers sont ajoutes aux gardes d'architecture.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire le bloc evidence text / facts / labels encore present dans `State.cs`.
- Puis reprendre la reduction de `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-09 - extraction rendering exact item, facts et formatting evidence

Extraction realisee :

- Six blocs ont ete extraits de `ToolAgentOrchestrator.State.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.SourceBackedEvidenceSelectionQuery.cs` ;
  - `ToolAgentOrchestrator.SourceBackedExactItemAnswerRendering.cs` ;
  - `ToolAgentOrchestrator.SourceBackedExactItemDisplayTitle.cs` ;
  - `ToolAgentOrchestrator.SourceBackedExactItemCardAnswer.cs` ;
  - `ToolAgentOrchestrator.SourceBackedEvidenceFacts.cs` ;
  - `ToolAgentOrchestrator.SourceBackedEvidenceTextFormatting.cs`.

Responsabilites extraites :

- `SourceBackedEvidenceSelectionQuery` :
  - adaptation mecanique de la requete d'evidence quand une demande comparative doit etre focalisee.
- `SourceBackedExactItemAnswerRendering` :
  - rendu court d'une reponse exact item source-backed ;
  - delegation vers les variantes parametres ou fiche documentee.
- `SourceBackedExactItemDisplayTitle` :
  - choix d'un titre d'affichage lisible a partir des hits RAG ;
  - scoring et nettoyage de titres OCR/profils ;
  - protection contre les titres faibles, marketing ou run-on.
- `SourceBackedExactItemCardAnswer` :
  - rendu d'une fiche documentee a partir des hits exact item ;
  - selection des hits lies a la source principale ;
  - scoring des preuves visibles pour la fiche.
- `SourceBackedEvidenceFacts` :
  - extraction de facts depuis les content cards ;
  - deduplication et nettoyage des facts affichables.
- `SourceBackedEvidenceTextFormatting` :
  - labels multilingues source-backed ;
  - excerpts de controle ;
  - extraction de durees, quantites, segments itemises et etapes ;
  - formatting des extraits d'evidence.

Interpretation architecturale :

- Le travail ne change pas encore la strategie de decision finale.
- Il reduit la dette structurelle en separant le rendu et le formatting des mecanismes d'etat.
- Ces fichiers restent mecaniques : ils rendent, nettoient et formatent des preuves deja retenues, sans declarer semantiquement qu'une source repond a la question utilisateur.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.State.cs` descend a environ 3 754 lignes.
- `ToolAgentOrchestrator.cs` reste a environ 10 905 lignes et reste une cible majeure.
- Nouveaux fichiers :
  - evidence selection query environ 26 lignes ;
  - exact item answer rendering environ 84 lignes ;
  - exact item display title environ 489 lignes ;
  - exact item card answer environ 837 lignes ;
  - evidence facts environ 218 lignes ;
  - evidence text formatting environ 697 lignes.
- Les nouveaux fichiers sont ajoutes aux gardes d'architecture avec des plafonds explicites.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 20/20.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Continuer la reduction de `State.cs` si des blocs autonomes restent extractibles.
- Commencer ensuite la reduction de `ToolAgentOrchestrator.cs`, qui reste trop massif pour l'objectif final.
- Les prochaines extractions doivent rapprocher le code du flux canonique planner -> tools -> EvidenceBundle -> judge -> writer -> verifier -> repair -> UI.

## Mise a jour - 2026-07-09 - extraction fallback readable, relevance/profile et modeles RAG

Extraction realisee :

- Sept blocs supplementaires ont ete extraits de `ToolAgentOrchestrator.State.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.SourceBackedReadableFallbacks.cs` ;
  - `ToolAgentOrchestrator.SourceBackedPlanItemTitleExtractionV2.cs` ;
  - `ToolAgentOrchestrator.SourceBackedRagHitRelevanceAndProfile.cs` ;
  - `ToolAgentOrchestrator.SourceBackedBackendGuidanceFallbacks.cs` ;
  - `ToolAgentOrchestrator.SourceBackedRagModels.cs` ;
  - `ToolAgentOrchestrator.StatsFallback.cs` ;
  - `ToolAgentOrchestrator.JsonHelpers.cs`.

Responsabilites extraites :

- `SourceBackedReadableFallbacks` :
  - fallback readable source-backed ;
  - suppression ou preference de certains fallbacks partiels ;
  - reponses quand une evidence obligatoire ou une ancre manque.
- `SourceBackedPlanItemTitleExtractionV2` :
  - extraction de titres d'items depuis les textes RAG ;
  - nettoyage OCR ;
  - filtrage des titres bruités.
- `SourceBackedRagHitRelevanceAndProfile` :
  - scoring lexical des hits RAG ;
  - detection de sujets techniques courts ;
  - classification du profil d'evidence ;
  - selection hints et termes de signal de requete.
- `SourceBackedBackendGuidanceFallbacks` :
  - fallback voisin RAG ;
  - clarification guidee par le backend quand les sources ne suffisent pas ;
  - preference mecanique entre source-backed answer et clarification backend.
- `SourceBackedRagModels` :
  - records internes `RagHitSummary`, content card, evidence facts et profile.
- `StatsFallback` et `JsonHelpers` :
  - sortie des helpers stats/json qui n'avaient plus leur place dans un fichier d'etat.

Interpretation architecturale :

- `State.cs` n'est plus le conteneur principal des helpers source-backed.
- Les blocs extraits restent des services mecaniques : scoring, formatage, fallback, modeles, helper json.
- Le LLM garde la responsabilite de l'orchestration semantique dans le flux cible ; ce refactor retire surtout du bruit et rend les chemins deterministes auditable.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.State.cs` descend a environ 1 963 lignes.
- `ToolAgentOrchestrator.cs` reste a environ 10 905 lignes et devient maintenant la cible prioritaire.
- Nouveaux fichiers :
  - readable fallbacks environ 493 lignes ;
  - plan item title extraction V2 environ 290 lignes ;
  - RAG hit relevance/profile environ 657 lignes ;
  - backend guidance fallbacks environ 220 lignes ;
  - RAG models environ 132 lignes ;
  - stats fallback environ 85 lignes ;
  - json helpers environ 27 lignes.
- Les nouveaux fichiers source-backed sont ajoutes aux gardes d'architecture.
- Un garde-fou dedie protege aussi `StatsFallback.cs` et `JsonHelpers.cs`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK apres ajout du namespace `SAAIA.Client.WinUI.Localization` dans `StatsFallback.cs`.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 21/21.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 39/39.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Auditer les 1 963 lignes restantes de `State.cs` pour separer runtime snapshot, clarification/reparation et source derivation si cela reste pertinent.
- Puis attaquer `ToolAgentOrchestrator.cs`, encore trop massif pour une structure professionnelle durable.

## Mise a jour - 2026-07-09 - finalisation du nettoyage de State.cs

Extraction realisee :

- Les responsabilites restantes de `ToolAgentOrchestrator.State.cs` ont ete separees en fichiers dedies.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.RuntimeSnapshot.cs` ;
  - `ToolAgentOrchestrator.ClarificationState.cs` ;
  - `ToolAgentOrchestrator.ResponseRepairAndCompletion.cs` ;
  - `ToolAgentOrchestrator.TurnMemoryState.cs` ;
  - `ToolAgentOrchestrator.SourceRefDerivation.cs` ;
  - `ToolAgentOrchestrator.SourceBackedBroadSynthesisGates.cs`.

Responsabilites extraites :

- `RuntimeSnapshot` :
  - snapshot runtime expose aux outils ;
  - resume memoire agent.
- `ClarificationState` :
  - preparation et recuperation des clarifications pendantes ;
  - detection des confirmations de recherche elargie ;
  - gestion mecanique de la clarification en memoire.
- `ResponseRepairAndCompletion` :
  - generation de clarification ;
  - verification de langue de sortie ;
  - generation de reparation ;
  - completion texte commune.
- `TurnMemoryState` :
  - memorisation du dernier tour utilisateur/assistant.
- `SourceRefDerivation` :
  - parsing de l'enveloppe de reponse ;
  - conversion des resultats RAG/resolution en `ToolMemory.SourceRef` ;
  - derivation des sources visibles depuis les hits RAG.
- `SourceBackedBroadSynthesisGates` :
  - gating broad synthesis ;
  - detection no-data/degenerate ;
  - couverture minimale et richesse d'evidence ;
  - conditions de writer pour synthese large.

Interpretation architecturale :

- `State.cs` ne contient plus que le reset d'etat de tour.
- La dette monolithique de `State.cs` est pratiquement resorbee.
- Les chemins source-backed restants sont maintenant localisables par responsabilite au lieu d'etre caches dans un fichier d'etat massif.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.State.cs` descend a environ 33 lignes.
- `ToolAgentOrchestrator.cs` reste a environ 10 905 lignes et devient le principal probleme structurel actif.
- Nouveaux fichiers :
  - runtime snapshot environ 260 lignes ;
  - clarification state environ 314 lignes ;
  - response repair/completion environ 191 lignes ;
  - turn memory state environ 58 lignes ;
  - source ref derivation environ 514 lignes ;
  - broad synthesis gates environ 715 lignes.
- Nouveaux garde-fous :
  - `State.cs` plafonne a 80 lignes ;
  - runtime/clarification/reparation/turn memory plafonnes ;
  - `SourceRefDerivation.cs` ajoute aux partiels de sources ;
  - `SourceBackedBroadSynthesisGates.cs` ajoute aux partiels source-backed.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 22/22.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 40/40.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Commencer la reduction de `ToolAgentOrchestrator.cs`, environ 10 905 lignes.
- Priorite : identifier les blocs du flux actif qui peuvent etre separes sans changer le comportement, puis poser les memes garde-fous de taille et de responsabilite.

## Mise a jour - 2026-07-09 - premiere reduction de ToolAgentOrchestrator.cs

Extraction realisee :

- Trois blocs ont ete extraits de `ToolAgentOrchestrator.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.LlmCompletion.cs` ;
  - `ToolAgentOrchestrator.RunPipeline.cs` ;
  - `ToolAgentOrchestrator.SourceBackedFallbackRouting.cs`.

Responsabilites extraites :

- `LlmCompletion` :
  - completion LLM avec retry ;
  - streaming/fallback writer.
- `RunPipeline` :
  - pipeline principal de tour `RunAsync` ;
  - orchestration top-level du router, tools, source-backed pipeline, writer, repair et final return.
- `SourceBackedFallbackRouting` :
  - traces de finalizer source-backed ;
  - fallbacks no-evidence/timeout/overflow ;
  - derivation de sources pour fallback source-backed ;
  - detection des fragments documentaires ambigus.

Interpretation architecturale :

- Le flux principal est maintenant dans un partial nomme par responsabilite au lieu d'etre enfoui dans le fichier racine.
- `ToolAgentOrchestrator.cs` commence a redevenir un fichier de composition et d'infrastructure, meme s'il reste encore trop gros.
- Les tests d'architecture ont ete adaptes pour inspecter le partial actif `RunPipeline.cs` au lieu de supposer que le flux canonique reste dans le fichier principal.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.cs` descend a environ 9 498 lignes.
- Nouveaux fichiers :
  - LLM completion environ 123 lignes ;
  - run pipeline environ 1 055 lignes ;
  - source-backed fallback routing environ 304 lignes.
- Nouveau garde-fou :
  - `ToolAgentOrchestrator.cs` plafonne temporairement a 10 000 lignes ;
  - les trois nouveaux partials sont plafonnes ;
  - les tests verifient que `CompleteWithRetryAsync`, `RunAsync` et les traces/fallbacks extraits ne reviennent pas dans le fichier principal.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire les gros blocs d'inventory/deterministic answers/documentary probe/router encore presents dans `ToolAgentOrchestrator.cs`.
- Abaisser progressivement le plafond du fichier principal apres chaque extraction stable.

## Mise a jour - 2026-07-09 - extraction inventory et reponses deterministes

Extraction realisee :

- Trois blocs supplementaires ont ete extraits de `ToolAgentOrchestrator.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.InventoryRendering.cs` ;
  - `ToolAgentOrchestrator.DeterministicToolAnswers.cs` ;
  - `ToolAgentOrchestrator.InlineSourceInjection.cs`.

Responsabilites extraites :

- `InventoryRendering` :
  - execution locale `meta.list_questions` ;
  - rendu documents list/tree ;
  - payloads inventory pour documents, categories, stats, summaries, extraction quality, pages, empty folders et diagnostic performance.
- `DeterministicToolAnswers` :
  - fallback stats depuis resultats outils ;
  - emission de texte deterministe ;
  - reponse `sources.resolve` ;
  - reponses deterministes count/list/status.
- `InlineSourceInjection` :
  - injection de sources inline ;
  - suppression des listes de sources emises par le modele ;
  - nettoyage des blocs de sources finaux.

Interpretation architecturale :

- Ces blocs sont purement mecaniques et UI/outil : ils ne doivent pas decider si une source repond semantiquement a la demande.
- Le fichier principal perd une grande zone de rendu deterministe qui masquait le flux d'orchestration.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.cs` descend a environ 7 917 lignes.
- Nouveaux fichiers :
  - inventory rendering environ 1 141 lignes ;
  - deterministic tool answers environ 336 lignes ;
  - inline source injection environ 179 lignes.
- Le garde-fou `ToolAgentOrchestrator.cs` est abaisse de 10 000 a 8 000 lignes.
- Les nouveaux fichiers sont ajoutes aux garde-fous de taille et de non-retour dans le fichier principal.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire les blocs documentary probe, source policy shortcut, router et normalisation encore presents dans `ToolAgentOrchestrator.cs`.
- Continuer a abaisser le plafond du fichier principal progressivement.

## Mise a jour - 2026-07-09 - extraction documentary probe et source policy shortcut

Extraction realisee :

- Deux blocs actifs ont ete extraits de `ToolAgentOrchestrator.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.DocumentaryProbe.cs` ;
  - `ToolAgentOrchestrator.SourcePolicyShortcut.cs`.

Responsabilites extraites :

- `DocumentaryProbe` :
  - decision de lancer une probe documentaire ;
  - expansion/coverage de retrieval documentaire ;
  - choix d'utiliser le writer pour une probe ;
  - construction des requetes de probe ;
  - construction d'un `ToolResults` RAG depuis des hits.
- `SourcePolicyShortcut` :
  - shortcut source-policy avant router ;
  - refus des demandes de bypass source ou d'invention non supportee ;
  - ancrage source optionnel quand la politique le permet ;
  - skip exact-item pre-router.

Interpretation architecturale :

- Ces deux blocs sont actifs mais clairement separes du coeur de composition.
- La politique source reste mecanique : elle bloque les demandes incompatibles avec le contrat de sources, sans juger semantiquement la pertinence documentaire.
- La documentary probe reste une orchestration d'outils et de coverage, separee du LLM judge/writer canonique.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.cs` descend a environ 7 494 lignes.
- Nouveaux fichiers :
  - documentary probe environ 277 lignes ;
  - source policy shortcut environ 196 lignes.
- Le garde-fou `ToolAgentOrchestrator.cs` est abaisse de 8 000 a 7 600 lignes.
- Les nouveaux fichiers sont ajoutes aux garde-fous de taille et de non-retour dans le fichier principal.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire le bloc router et ses helpers de repair/normalisation depuis `ToolAgentOrchestrator.cs`.
- Continuer ensuite avec `AnswerAsync`, candidate adjudication, repair writer et normalisation generale.

## Mise a jour - 2026-07-09 - extraction router core, repair et prompts

Extraction realisee :

- Trois blocs router actifs ont ete extraits de `ToolAgentOrchestrator.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.RouterCore.cs` ;
  - `ToolAgentOrchestrator.RouterRepair.cs` ;
  - `ToolAgentOrchestrator.RouterPromptsAndMemory.cs`.

Responsabilites extraites :

- `RouterCore` :
  - orchestration `RouterAsync` ;
  - appel LLM router ;
  - fallback router `no_json`, `timeout` et `error` ;
  - parsing du plan router renvoye par le LLM.
- `RouterRepair` :
  - parse/sanitize du `RouterPlan` ;
  - repair structure du plan de recherche router ;
  - fallback par axes de recherche ;
  - prompts de repair router.
- `RouterPromptsAndMemory` :
  - prompt system compact source-backed du router ;
  - prompt utilisateur router ;
  - contexte memoire router ;
  - hints canoniques de categorie/document ;
  - description des actions d'outils.

Interpretation architecturale :

- Le router reste une couche d'orchestration d'outils et de strategie de recherche.
- Le code ne juge pas si une source repond semantiquement a la question utilisateur.
- Les repairs router couvrent les manques mecaniques de structure, sans retirer au LLM le role de decideur semantique.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.cs` descend a environ 6 454 lignes au compteur `File.ReadLines`.
- Nouveaux fichiers :
  - router core environ 201 lignes ;
  - router repair environ 660 lignes ;
  - router prompts/memory environ 254 lignes.
- Le garde-fou `ToolAgentOrchestrator.cs` est abaisse de 7 600 a 6 500 lignes.
- Les nouveaux fichiers sont ajoutes aux garde-fous de taille et de non-retour dans le fichier principal.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire `ExecuteToolsAsync` et le flux d'execution d'outils hors du fichier principal.
- Extraire ensuite `AnswerAsync` dans une couche de composition lisible.
- Continuer a isoler candidate adjudication, writer repair, verification source et normalisation pour converger vers le pipeline canonique `EvidenceBundle`.

## Mise a jour - 2026-07-09 - extraction ToolExecutionPipeline

Extraction realisee :

- `ExecuteToolsAsync` a ete extrait de `ToolAgentOrchestrator.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.ToolExecutionPipeline.cs`.

Responsabilites extraites :

- execution sequentielle des tool calls choisis par le router ;
- application de la scope categorie RAG resolue par le LLM avant `rag.multi_search` ;
- emission des traces `tool.start` et `tool.end` ;
- gestion mecanique `unknown_tool`, `unknown_tool_handler`, `admin_required` ;
- appel des handlers d'outils existants ;
- classification mecanique des erreurs d'outils.

Interpretation architecturale :

- Cette extraction ne change pas la strategie de recherche.
- Le code reste au niveau execution, contrats, traces et erreurs mecaniques.
- Le LLM conserve le role de decideur semantique du plan et des recherches.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Correction de mesure :

- Le compteur utilise par les tests d'architecture est `File.ReadLines`, qui compte aussi les lignes vides.
- Les chiffres non vides remontes pendant une validation intermediaire ont ete remplaces dans l'audit par les chiffres reels du garde-fou.
- Le palier router est donc documente a environ 6 454 lignes pour `ToolAgentOrchestrator.cs`, pas 5 856.

Etat structurel :

- `ToolAgentOrchestrator.cs` est maintenant a environ 6 313 lignes au compteur `File.ReadLines`.
- `ToolAgentOrchestrator.ToolExecutionPipeline.cs` fait environ 157 lignes.
- `ToolAgentOrchestrator.ToolExecution.cs` reste a environ 803 lignes.
- Le garde-fou `ToolAgentOrchestrator.cs` est abaisse de 6 500 a 6 350 lignes.
- Le test d'architecture verifie que `ExecuteToolsAsync` ne revient pas dans le fichier principal.
- Le BOM UTF-8 introduit pendant l'extraction a ete retire.
- Le commentaire accentue abime par l'encodage a ete remplace par une version ASCII.
- Le warning nullable sur `onDelta(finalAnswer)` dans `AnswerAsync` a ete corrige par annotation non-null apres le garde `string.IsNullOrWhiteSpace`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire `AnswerAsync` dans un partial de composition de reponse.
- Ensuite isoler les sous-blocs encore melanges : bypass writer, finalizer source-backed, repair writer, verification source et normalisation.
- Maintenir la meme regle : aucune decision semantique de pertinence source dans le code, seulement des contrats et verifications mecaniques.

## Mise a jour - 2026-07-09 - extraction AnswerPipeline

Extraction realisee :

- `AnswerAsync` a ete extrait de `ToolAgentOrchestrator.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.AnswerPipeline.cs`.

Responsabilites extraites :

- composition de reponse apres execution des outils ;
- preparation du contexte writer ;
- bypass writer encore actifs mais maintenant isoles dans le fichier de composition ;
- garde-fous source-backed avant/apres writer ;
- appel writer LLM ;
- critic pass ;
- repairs writer ;
- derivation et reconciliation des sources finales.

Interpretation architecturale :

- Cette extraction ne change pas la logique de reponse.
- Elle sort la couche de composition de reponse du fichier principal pour rendre le chemin principal plus lisible.
- `AnswerPipeline` reste volontairement un palier intermediaire : le fichier fait encore environ 1 319 lignes et devra etre decoupe en sous-responsabilites.
- Le prochain decoupage devra separer les bypass writer, les finalizers source-backed, le critic/repair, et la verification/reconciliation source.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.cs` est maintenant a environ 5 008 lignes au compteur `File.ReadLines`.
- `ToolAgentOrchestrator.AnswerPipeline.cs` fait environ 1 319 lignes.
- `ToolAgentOrchestrator.ToolExecutionPipeline.cs` reste a environ 157 lignes.
- Le garde-fou `ToolAgentOrchestrator.cs` est abaisse de 6 350 a 5 050 lignes.
- Le nouveau garde-fou `AnswerPipeline` est fixe a 1 500 lignes pour empecher le fichier de grossir davantage.
- Le test d'architecture verifie que `AnswerAsync` ne revient pas dans le fichier principal.
- Les fichiers verifies sont sans BOM UTF-8.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Decouper `AnswerPipeline` en sous-partials plus courts.
- Cibles probables :
  - `AnswerWriterContext` ;
  - `AnswerWriterBypasses` ;
  - `AnswerSourceBackedGuards` ;
  - `AnswerWriterFinalization` ;
  - `AnswerPostWriterRepair`.
- Conserver le meme contrat : le code verifie et repare mecaniquement, le LLM juge la pertinence semantique.

## Mise a jour - 2026-07-09 - extraction AnswerWriterBypasses

Extraction realisee :

- Les bypass precoces du writer ont ete extraits de `ToolAgentOrchestrator.AnswerPipeline.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.AnswerWriterBypasses.cs`.

Responsabilites extraites :

- clarification backend issue d'un guidage RAG deja produit ;
- reponse de tracabilite version quand elle est deja resolue ;
- garde source-policy mecanique ;
- inventaire deterministe deja rendu ;
- refus exact-item quand les hits RAG ne contiennent pas le titre demande.

Interpretation architecturale :

- Ces bypass restent des sorties mecaniques ou deja resolues, pas des jugements semantiques generaux de pertinence source.
- Le code ne choisit pas une source comme reponse a la question ; il traite seulement des cas de contrat, d'inventaire ou d'impossibilite mecanique.
- Le chemin LLM writer principal devient plus lisible dans `AnswerPipeline`.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.AnswerPipeline.cs` descend a environ 1 250 lignes.
- `ToolAgentOrchestrator.AnswerWriterBypasses.cs` fait environ 100 lignes.
- `ToolAgentOrchestrator.cs` reste a environ 5 008 lignes.
- Le garde-fou `AnswerPipeline` est abaisse de 1 500 a 1 300 lignes.
- Le nouveau fichier est ajoute aux garde-fous de taille.
- Le test d'architecture verifie que les bypass `backend_guidance_ask_clarification` et `writer_bypass_missing_exact_item` ne reviennent pas dans `AnswerPipeline`.
- Les fichiers verifies sont sans BOM UTF-8.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire ensuite la preparation du contexte writer ou les garde-fous source-backed pre-writer.
- Garder `AnswerPipeline` sous pression jusqu'a ce que la methode principale lise comme une orchestration courte.

## Mise a jour - 2026-07-10 - extraction AnswerWriterContext

Extraction realisee :

- La preparation du contexte writer a ete extraite de `ToolAgentOrchestrator.AnswerPipeline.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.AnswerWriterContext.cs`.

Responsabilites extraites :

- choix du prompt writer compact/source-backed ;
- construction de l'adjudication candidate pour le writer ;
- expansion eventuelle apres adjudication candidate ;
- rafraichissement des resultats writer/evidence apres expansion ;
- construction de l'inventaire prive de preuves ;
- construction du bloc `TOOL_RESULTS` pour le prompt writer ;
- construction des messages `system` / `user` envoyes au writer LLM ;
- conservation du JSON d'adjudication pour les repairs posterieurs.

Interpretation architecturale :

- Cette extraction prepare le contexte du writer, elle ne change pas la decision semantique finale.
- L'adjudication candidate reste une entree de contexte et de trace pour le writer/repair.
- Le LLM reste responsable de juger et rediger a partir des preuves disponibles.
- Le code reste sur budget, format de prompt, inventaire, traces et transport de donnees.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.AnswerPipeline.cs` descend a environ 1 144 lignes.
- `ToolAgentOrchestrator.AnswerWriterContext.cs` fait environ 182 lignes.
- `ToolAgentOrchestrator.AnswerWriterBypasses.cs` reste a environ 100 lignes.
- `ToolAgentOrchestrator.cs` reste a environ 5 008 lignes.
- Le garde-fou `AnswerPipeline` est abaisse de 1 300 a 1 150 lignes.
- Le nouveau fichier est ajoute aux garde-fous de taille avec un plafond de 250 lignes.
- Le test d'architecture verifie que `PRIVATE_SOURCE_CANDIDATE_ADJUDICATION` et `writer.evidence_inventory.roster` ne reviennent pas dans `AnswerPipeline`.
- Les fichiers verifies sont sans BOM UTF-8.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire les garde-fous source-backed pre-writer ou le bloc d'appel writer LLM/timeouts.
- Continuer jusqu'a ce que `AnswerPipeline` devienne une orchestration lisible et courte.

## Mise a jour - 2026-07-10 - extraction AnswerWriterLlm

Extraction realisee :

- Le bloc d'appel writer LLM a ete extrait de `ToolAgentOrchestrator.AnswerPipeline.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.AnswerWriterLlm.cs`.

Responsabilites extraites :

- timebox de l'appel writer LLM quand des resultats RAG sont presents ;
- emission des traces `writer.llm.start` et `writer.llm.end` ;
- fallback mecanique sur timeout writer ;
- detection overflow contexte LLM ;
- retry compact apres overflow ;
- fallback terminal source-backed quand le writer ne peut pas produire de reponse ;
- preservation du rethrow original via `ExceptionDispatchInfo` quand l'overflow ne peut pas etre compense.

Interpretation architecturale :

- Cette extraction ne change pas la decision semantique du writer.
- Le nouveau fichier contient la mecanique d'appel, de timeout, de retry et de fallback autour du writer.
- Les traces du chemin writer restent visibles dans les partials `ToolAgentOrchestrator*.cs`.
- Le code reste sur execution, budget/timeout et erreurs mecaniques.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.AnswerPipeline.cs` descend a environ 1 006 lignes.
- `ToolAgentOrchestrator.AnswerWriterLlm.cs` fait environ 227 lignes.
- `ToolAgentOrchestrator.AnswerWriterContext.cs` reste a environ 182 lignes.
- `ToolAgentOrchestrator.cs` reste a environ 5 008 lignes.
- Le garde-fou `AnswerPipeline` est abaisse de 1 150 a 1 050 lignes.
- Le nouveau fichier est ajoute aux garde-fous de taille avec un plafond de 300 lignes.
- Le test d'architecture verifie que `writer.llm.overflow_retry.start` et `writer.llm.timeout.terminal_fallback` ne reviennent pas dans `AnswerPipeline`.
- Les fichiers verifies sont sans BOM UTF-8.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire les garde-fous source-backed pre-writer ou le bloc post-writer/source reconciliation.
- Continuer a faire descendre `AnswerPipeline` vers une orchestration courte.

## Mise a jour - 2026-07-10 - extraction AnswerPostWriterRag

Extraction realisee :

- Le gros bloc post-writer RAG/source-backed a ete extrait de `ToolAgentOrchestrator.AnswerPipeline.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.AnswerPostWriterRag.cs`.

Responsabilites extraites :

- detection de sortie writer degeneree ;
- derivation initiale des sources depuis les resultats RAG ;
- traces `writer.post.sources.*` ;
- gate final structure pour les reponses source-backed de planification ;
- acceptation mecanique des citations visibles quand elles sont prouvables ;
- analyse support/source des items de planification ;
- repair writer quand le support source est insuffisant ;
- fallback d'insuffisance quand le contrat source ne peut pas etre satisfait ;
- garde-fous pre-critic pour no-rag-data, options surpromues, planning sous-utilise, planning pauvre et items non supportes.

Interpretation architecturale :

- Le bloc extrait reste une couche post-writer de verification/reconciliation mecanique.
- Il ne transforme pas le code en juge semantique general de pertinence source.
- Les repairs restent appeles via writer LLM quand une decision/redaction doit etre reprise.
- Les refus/fallbacks sont lies au contrat source, aux citations visibles et aux preuves disponibles.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.AnswerPipeline.cs` descend a environ 547 lignes.
- `ToolAgentOrchestrator.AnswerPostWriterRag.cs` fait environ 520 lignes.
- `ToolAgentOrchestrator.AnswerWriterLlm.cs` reste a environ 227 lignes.
- `ToolAgentOrchestrator.cs` reste a environ 5 008 lignes.
- Le garde-fou `AnswerPipeline` est abaisse de 1 050 a 600 lignes.
- Le nouveau fichier est ajoute aux garde-fous de taille avec un plafond de 600 lignes.
- Le test d'architecture verifie que `writer.final_structured_gate.start` et `writer.post.sources.start` ne reviennent pas dans `AnswerPipeline`.
- Les fichiers verifies sont sans BOM UTF-8.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- `AnswerPostWriterRag.cs` est maintenant le prochain fichier a recouper, car il fait encore environ 520 lignes.
- Extraire probablement le structured final gate ou les guards pre-critic dans un fichier separe.
- Continuer ensuite vers critic/post-critic/source alignment pour rapprocher le flux du contrat canonique `EvidenceBundle -> verifier -> repair`.

## Mise a jour - 2026-07-10 - extraction AnswerPostWriterStructuredGate

Extraction realisee :

- Le structured final gate a ete extrait de `ToolAgentOrchestrator.AnswerPostWriterRag.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.AnswerPostWriterStructuredGate.cs`.

Responsabilites extraites :

- gate final des reponses structurees source-backed ;
- verification des citations visibles ;
- analyse du support source des items de planification ;
- repair writer si le support est insuffisant ;
- retry de repair avec feedback structure ;
- fallback d'insuffisance quand le contrat source ne peut pas etre prouve ;
- traces `writer.final_structured_gate.*`.

Interpretation architecturale :

- Cette extraction isole le verifier/reconcile structurel post-writer.
- Le code reste sur verification mecanique du contrat source et des citations visibles.
- Quand une decision/redaction doit etre reprise, le repair reste confie au writer LLM.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.AnswerPostWriterRag.cs` descend a environ 286 lignes.
- `ToolAgentOrchestrator.AnswerPostWriterStructuredGate.cs` fait environ 281 lignes.
- `ToolAgentOrchestrator.AnswerPipeline.cs` reste a environ 547 lignes.
- Le garde-fou `AnswerPostWriterRag` est abaisse de 600 a 320 lignes.
- Le nouveau fichier est ajoute aux garde-fous de taille avec un plafond de 320 lignes.
- Le test d'architecture verifie que `writer.final_structured_gate.repair.start` ne revient pas dans `AnswerPostWriterRag`.
- Les fichiers verifies sont sans BOM UTF-8.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire ensuite les guards post-critic/source alignment depuis `AnswerPipeline`.
- Continuer a rapprocher la fin du flux d'un vrai contrat canonique source verifier -> repair -> UI payload.

## Mise a jour - 2026-07-10 - extraction AnswerPostCriticSourceAlignment

Extraction realisee :

- Les guards post-critic et la reconciliation finale des sources ont ete extraits de `ToolAgentOrchestrator.AnswerPipeline.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.AnswerPostCriticSourceAlignment.cs`.

Responsabilites extraites :

- critic pass apres writer quand applicable ;
- repair post-critic des reponses de planification pauvres ;
- fallback mecanique quand le critic/post-critic ne peut pas satisfaire le contrat source ;
- nettoyage des sources quand une reponse signale un item exact introuvable ;
- garde des items de planification non supportes ;
- repair de fuite de consignes writer ;
- final support check pour les reponses documentaires ;
- reconciliation des sources visibles requises avec la reponse finale ;
- repair source-alignment si des sources visibles obligatoires ne sont pas citees.

Interpretation architecturale :

- Cette extraction isole la fin du contrat `source verifier -> repair`.
- Le code reste sur verification mecanique des citations, des sources visibles et des preuves supportees.
- Les corrections redactionnelles restent confiees au writer LLM via repair.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.AnswerPipeline.cs` descend a environ 336 lignes.
- `ToolAgentOrchestrator.AnswerPostCriticSourceAlignment.cs` fait environ 268 lignes.
- `ToolAgentOrchestrator.AnswerPostWriterRag.cs` reste a environ 286 lignes.
- `ToolAgentOrchestrator.AnswerPostWriterStructuredGate.cs` reste a environ 281 lignes.
- Le garde-fou `AnswerPipeline` est abaisse de 600 a 360 lignes.
- Le nouveau fichier est ajoute aux garde-fous de taille avec un plafond de 320 lignes.
- Le test d'architecture verifie que `post_critic_guard.poor_planning_deterministic.skipped` et `post_writer_guard_source_alignment_repaired` ne reviennent pas dans `AnswerPipeline`.
- Les fichiers verifies sont sans BOM UTF-8.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- `AnswerPipeline` est maintenant une orchestration relativement courte.
- Prochaine etape utile : audit structurel du pipeline global pour identifier les derniers gros fichiers ou chemins herites actifs restants avant validation UI.

## Mise a jour - 2026-07-10 - extraction SourceBackedCandidateAdjudication

Extraction realisee :

- Le bloc d'adjudication privee des candidats source-backed a ete sorti de `ToolAgentOrchestrator.cs`.
- Trois fichiers specialises ont ete crees :
  - `ToolAgentOrchestrator.SourceBackedCandidateAdjudication.cs` ;
  - `ToolAgentOrchestrator.SourceBackedCandidateAdjudicationPrompts.cs` ;
  - `ToolAgentOrchestrator.SourceBackedCandidateAdjudicationSignal.cs`.

Responsabilites separees :

- Runtime LLM d'adjudication candidate :
  - lancement du prompt prive ;
  - timeout ;
  - compact retry en cas d'overflow contexte ;
  - traces `writer.candidate_adjudication.*`.
- Prompts et budget d'adjudication :
  - system prompt ;
  - user prompt ;
  - schema JSON attendu ;
  - seuils de compaction.
- Parsing et signal d'adjudication :
  - normalisation JSON ;
  - extraction de la decision ;
  - comptage des candidats utiles ;
  - detection des slots manquants ;
  - signal indiquant si le LLM demanderait plus de retrieval.

Interpretation architecturale :

- Le LLM reste juge semantique des candidats.
- Le code ne decide pas qu'une source repond a la question utilisateur ; il structure le prompt, verifie le JSON, transporte le diagnostic et trace le chemin.
- La relance RAG directe reste explicitement neutralisee par le contrat canonique actuel : trace `canonical_pipeline_owns_retrieval`.
- Aucun hardcoding cuisine/categorie n'a ete ajoute.

Incident corrige pendant l'extraction :

- Une premiere extraction mecanique a retire les constantes avant d'utiliser les index de bloc, ce qui a abime temporairement l'en-tete de `TryRepairSourceBackedSynthesisAnswerWithWriterAsync`.
- Correction appliquee :
  - restauration de l'en-tete du repair writer ;
  - redécoupage du fichier d'adjudication en trois partials courts ;
  - restauration manuelle du bloc de parsing/signal dans `SourceBackedCandidateAdjudicationSignal.cs`.
- Le build complet a ensuite valide que l'etat final est sain.

Etat structurel :

- `ToolAgentOrchestrator.cs` descend d'environ 5 008 lignes a 4 333 lignes.
- `ToolAgentOrchestrator.SourceBackedCandidateAdjudication.cs` fait environ 221 lignes.
- `ToolAgentOrchestrator.SourceBackedCandidateAdjudicationPrompts.cs` fait environ 132 lignes.
- `ToolAgentOrchestrator.SourceBackedCandidateAdjudicationSignal.cs` fait environ 352 lignes.
- Le garde-fou du fichier principal est abaisse a 4 350 lignes.
- Les trois nouveaux partials sont ajoutes aux garde-fous d'architecture.
- Le test d'architecture verifie que les methodes d'adjudication ne reviennent pas dans `ToolAgentOrchestrator.cs`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Continuer la reduction de `ToolAgentOrchestrator.cs`, qui reste encore a 4 333 lignes.
- Prochaine extraction probable : bloc repair writer source-backed, normalisation des args/outils, ou defaults RAG documentaires selon la cohesion observee au prochain audit de methodes.

## Mise a jour - 2026-07-10 - extraction SourceBackedRepairWriter

Extraction realisee :

- Le bloc `repair writer` source-backed a ete sorti de `ToolAgentOrchestrator.cs`.
- Trois fichiers specialises ont ete crees :
  - `ToolAgentOrchestrator.SourceBackedRepairWriter.cs` ;
  - `ToolAgentOrchestrator.SourceBackedRepairWriterCitationIds.cs` ;
  - `ToolAgentOrchestrator.SourceBackedRepairWriterPrompts.cs`.

Responsabilites separees :

- Runtime de repair writer :
  - verification des conditions de repair ;
  - selection de l'evidence inventory ;
  - appel LLM de repair ;
  - retry compact en cas d'overflow contexte ;
  - acceptation/rejet mecanique de la reponse reparee ;
  - traces `writer.repair.*`.
- Citation ids / source ids :
  - ajout cible des ids `[E#]` quand un brouillon structure contient des items concrets sans citation ;
  - conversion ensuite vers les citations visibles.
- Prompts et helpers de repair :
  - prompt utilisateur du repair writer ;
  - contrat de planification structuree ;
  - compaction de l'adjudication candidate pour le prompt ;
  - note de contexte de suivi utilisateur ;
  - limitation du roster de preuves.

Interpretation architecturale :

- Le repair writer est maintenant un sous-chemin explicite du contrat `writer -> verifier -> repair`.
- Le code garde son role mecanique : verifier le format, les citations visibles, les overflows, les garde-fous et les traces.
- La decision semantique sur l'utilisation des preuves visibles reste dans le LLM de repair.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.cs` descend d'environ 4 333 lignes a 3 618 lignes.
- `ToolAgentOrchestrator.SourceBackedRepairWriter.cs` fait environ 358 lignes.
- `ToolAgentOrchestrator.SourceBackedRepairWriterCitationIds.cs` fait environ 162 lignes.
- `ToolAgentOrchestrator.SourceBackedRepairWriterPrompts.cs` fait environ 228 lignes.
- Le garde-fou du fichier principal est abaisse a 3 650 lignes.
- Les trois nouveaux partials sont ajoutes aux garde-fous d'architecture.
- Le test d'architecture verifie que le repair writer ne revient pas dans `ToolAgentOrchestrator.cs`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- `ToolAgentOrchestrator.cs` reste encore trop grand, mais les chemins writer/adjudication/repair sont maintenant beaucoup plus lisibles.
- Prochain audit : normalisation d'args/outils, critic pass, summary flow, ou fallbacks source-backed restants selon la cohesion du bloc.

## Mise a jour - 2026-07-10 - extraction ToolPlanNormalization

Extraction realisee :

- Le bloc de normalisation des appels outils a ete sorti de `ToolAgentOrchestrator.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.ToolPlanNormalization.cs`.

Responsabilites extraites :

- `SanitizeToolCalls` ;
- normalisation des noms d'outils ;
- normalisation des arguments par outil ;
- bornage des limites/offsets/topK ;
- normalisation des modes RAG, summary, export, langue document ;
- extraction generique de `docRef`, valeurs imbriquees et objets JSON d'arguments.

Interpretation architecturale :

- Ce bloc est du contrat d'outil deterministe, pas de la decision semantique.
- Il reste donc cote code : forme des appels, bornes, aliases de champs, compatibilite avec le backend.
- La decision de quelles recherches/outils utiliser reste dans le router/planner LLM et les etapes source-backed.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.cs` descend d'environ 3 618 lignes a 3 230 lignes.
- `ToolAgentOrchestrator.ToolPlanNormalization.cs` fait environ 398 lignes.
- Le garde-fou du fichier principal est abaisse a 3 250 lignes.
- Le nouveau fichier est ajoute aux garde-fous d'architecture.
- Le test d'architecture verifie que `SanitizeToolCalls` et `NormalizeToolArgs` ne reviennent pas dans `ToolAgentOrchestrator.cs`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire ensuite `SanitizeRouterPlan` et les helpers router policy, ou isoler les gros blocs RAG documentaires encore dans `ToolAgentOrchestrator.cs`.
- Apres la reduction du fichier principal, attaquer les anciens partials encore superieurs a 1 000 lignes.

## Mise a jour - 2026-07-10 - extraction RouterPlanPolicy et fix requete RAG generique

Extraction realisee :

- Le bloc de normalisation/policy du plan routeur a ete sorti de `ToolAgentOrchestrator.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.RouterPlanPolicy.cs`.

Responsabilites extraites :

- `SanitizeRouterPlan` ;
- normalisation de l'intent routeur ;
- detection des outils RAG ;
- decision mecanique de forcer RAG quand le router LLM tombe en `chat.general` sans outil sur un sujet documentaire ;
- override mecanique d'une clarification quand la demande source-backed est deja suffisamment exploitable ;
- detection des requetes RAG trop faibles ;
- tokens faibles generiques a ignorer dans certaines heuristiques de requete.

Interpretation architecturale :

- Ce bloc normalise le contrat produit par le LLM routeur.
- Il ne choisit pas quelle source repond a la question ; il empeche seulement des plans invalides, vides ou incompatibles avec les outils.
- Les decisions semantiques source-backed restent dans les etapes LLM planner/evidence judge/writer.

Fix genere pendant validation :

- La suite `RagQueryNormalizationRegressionTests` a revele un defaut generique preexistant :
  - `l'inertage precisement` et sa variante accentuee gardaient l'adverbe de precision dans le topic RAG.
- Correction dans `NormalizeRagQueryForRetrieval` :
  - ajout d'un second passage robuste pour retirer les suffixes generiques `precisement`, `precisamente`, `exactement`, `exactly`, `precisely` et la variante accentuee via escape Unicode.
- Ce fix est generique et ne cible aucune categorie metier.

Etat structurel :

- `ToolAgentOrchestrator.cs` descend d'environ 3 230 lignes a 3 038 lignes.
- `ToolAgentOrchestrator.RouterPlanPolicy.cs` fait environ 202 lignes.
- Le garde-fou du fichier principal est abaisse a 3 050 lignes.
- Le nouveau fichier est ajoute aux garde-fous d'architecture.
- Le test d'architecture verifie que `SanitizeRouterPlan`, `NormalizeRouterIntent` et `ShouldForceRagForStandaloneTopic` ne reviennent pas dans `ToolAgentOrchestrator.cs`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38 apres correction.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- `ToolAgentOrchestrator.cs` est sous 3 050 lignes, mais contient encore les blocs initial source-backed probes, defaults RAG documentaires, category scope et finalization/helpers.
- Prochaine extraction probable : `BuildInitialSourceBackedPlanningProbeQueries` et ses helpers, puis `ApplyDocumentaryRagDefaults`.

## Mise a jour - 2026-07-10 - extraction InitialPlanningProbeQueries et RouterRagCategoryScope

Extraction realisee :

- Le bloc de generation des probes initiales source-backed a ete sorti de `ToolAgentOrchestrator.cs`.
- Le filtrage de `categoryScope` routeur a ete separe dans un fichier distinct.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.SourceBackedInitialPlanningProbeQueries.cs` ;
  - `ToolAgentOrchestrator.RouterRagCategoryScope.cs`.

Responsabilites extraites :

- Construction des requetes initiales de probe source-backed ;
- construction d'une probe d'intention a partir de la demande utilisateur ;
- nettoyage et scoring des probes ;
- suppression de probes de navigation/sommaire quand elles pollueraient une recherche de planification ;
- detection des termes faibles ou modificateurs ;
- validation mecanique du `categoryScope` routeur pour eviter qu'une phrase utilisateur/prompt leak soit traitee comme une categorie fiable.

Interpretation architecturale :

- Ce bloc produit des seeds generiques de retrieval ; il ne juge pas quelles sources repondent a la question.
- Les probes restent un demarrage mecanique pour aider le pipeline, tandis que le LLM planner/evidence judge garde la responsabilite semantique.
- L'extraction rend ce point beaucoup plus auditable, notamment pour verifier plus tard si des seeds deterministes sont trop agressifs.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.cs` descend d'environ 3 038 lignes a 2 644 lignes.
- `ToolAgentOrchestrator.SourceBackedInitialPlanningProbeQueries.cs` fait environ 360 lignes.
- `ToolAgentOrchestrator.RouterRagCategoryScope.cs` fait environ 52 lignes.
- Le garde-fou du fichier principal est abaisse a 2 670 lignes.
- Les deux nouveaux fichiers sont ajoutes aux garde-fous d'architecture.
- Le test d'architecture verifie que les probes initiales et `ResolveTrustedRouterRagCategoryScope` ne reviennent pas dans `ToolAgentOrchestrator.cs`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire `ApplyDocumentaryRagDefaults`, qui est le gros bloc restant au centre de `ToolAgentOrchestrator.cs`.
- Ensuite, extraire le category scope LLM initial et les helpers de retrieval precis encore restants.

## Mise a jour - 2026-07-10 - extraction AnswerCriticPass et DocumentSummaryFlow

Extraction realisee :

- Le critic pass writer a ete sorti de `ToolAgentOrchestrator.cs`.
- Le flow document summary route par le router a ete sorti de `ToolAgentOrchestrator.cs`.
- L'enum `DocumentSummaryRequestKind` a ete deplace avec le flow summary.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.AnswerCriticPass.cs` ;
  - `ToolAgentOrchestrator.DocumentSummaryFlow.cs`.

Responsabilites extraites :

- Critic pass :
  - decision d'eligibilite du critic ;
  - appel LLM critic ;
  - parsing de l'enveloppe critic ;
  - traces `writer.critic.*`.
- Document summary flow :
  - detection du type de demande summary/about/store/check ;
  - resolution de la reference document ;
  - clarification si le document manque ;
  - lancement du flow summary connu ;
  - finalisation de la reponse summary avec payload source.

Interpretation architecturale :

- Le critic pass devient une etape claire apres writer.
- Le flow summary document est separe du pipeline RAG source-backed principal.
- Le fichier principal garde moins de logique de branches annexes.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Incident corrige pendant validation :

- Le premier build apres extraction a echoue car `DocumentSummaryFlow.cs` n'avait pas le `using SAAIA.Client.WinUI.Localization;` necessaire pour `DeterministicAgentText`.
- Correction appliquee puis build relance avec succes.

Etat structurel :

- `ToolAgentOrchestrator.cs` descend d'environ 2 644 lignes a 2 316 lignes.
- `ToolAgentOrchestrator.AnswerCriticPass.cs` fait environ 145 lignes.
- `ToolAgentOrchestrator.DocumentSummaryFlow.cs` fait environ 204 lignes.
- Le garde-fou du fichier principal est abaisse a 2 330 lignes.
- Les deux nouveaux fichiers sont ajoutes aux garde-fous d'architecture.
- Le test d'architecture verifie que le critic pass et le document summary flow ne reviennent pas dans `ToolAgentOrchestrator.cs`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire `ApplyDocumentaryRagDefaults`, qui est maintenant le gros bloc central restant dans `ToolAgentOrchestrator.cs`.
- Ensuite, extraire le category scope LLM initial et les helpers de retrieval precis.

## Mise a jour - 2026-07-10 - extraction DocumentaryRagDefaults et SourceBackedRetrievalCaps

Extraction realisee :

- Le gros bloc `ApplyDocumentaryRagDefaults` a ete sorti de `ToolAgentOrchestrator.cs`.
- Les helpers de caps/topK source-backed ont ete separes.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.DocumentaryRagDefaults.cs` ;
  - `ToolAgentOrchestrator.SourceBackedRetrievalCaps.cs`.

Responsabilites extraites :

- Application des defaults RAG documentaires sur un `RouterPlan` ;
- conversion des demandes documentaires/source-backed en `rag.search` ou `rag.multi_search` ;
- enrichissement des requetes selon les formes de demande : item exact, comparaison, adaptation, contenu documentaire, recommandation, traceabilite de version, planification ;
- preservation du `categoryScope` fiable ;
- caps/topK generiques pour action, planning, comparaison et recherche single-rag.

Interpretation architecturale :

- Ce bloc reste deterministe parce qu'il adapte un plan d'outils, mais il ne choisit pas quelles sources repondent a la question.
- Le LLM garde la decision semantique dans les etapes planner/evidence judge/writer.
- L'extraction rend ce bloc visible comme point de vigilance : il reste a le decouper plus finement, car `DocumentaryRagDefaults.cs` fait encore environ 552 lignes.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel :

- `ToolAgentOrchestrator.cs` descend d'environ 2 316 lignes a 1 719 lignes.
- `ToolAgentOrchestrator.DocumentaryRagDefaults.cs` fait environ 552 lignes.
- `ToolAgentOrchestrator.SourceBackedRetrievalCaps.cs` fait environ 59 lignes.
- Le garde-fou du fichier principal est abaisse a 1 740 lignes.
- Les nouveaux fichiers sont ajoutes aux garde-fous d'architecture.
- Le test d'architecture verifie que `ApplyDocumentaryRagDefaults` et `NormalizeSourceBackedActionTopK` ne reviennent pas dans `ToolAgentOrchestrator.cs`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Decouper `DocumentaryRagDefaults.cs` lui-meme, car 552 lignes restent trop denses pour une structure finale irreprochable.
- Extraire ensuite le category scope LLM initial et les helpers de retrieval precis encore restants dans `ToolAgentOrchestrator.cs`.

## Mise a jour - 2026-07-10 - extraction category scope

Extraction realisee :

- La resolution du `categoryScope` RAG a ete sortie de `ToolAgentOrchestrator.cs`.
- L'application du `categoryScope` propose par le LLM initial a ete sortie de `ToolAgentOrchestrator.cs`.
- La resolution lexicale explicite du `categoryScope` a ete sortie de `ToolAgentOrchestrator.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.RagCategoryScopeResolution.cs` ;
  - `ToolAgentOrchestrator.SourceBackedInitialLlmCategoryScope.cs` ;
  - `ToolAgentOrchestrator.RagCategoryScopeLexical.cs`.

Responsabilites extraites :

- Resolution du scope de categorie depuis la memoire conversationnelle, le catalogue documentaire et les dernieres sources.
- Validation mecanique du scope propose par le LLM contre les categories connues.
- Reparation du `categoryScope` quand le plan LLM est lexicalement recuperable mais pas encore normalise.
- Application du scope dans les arguments `rag.search` / `rag.multi_search` sans choisir semantiquement les sources finales.
- Extraction lexicale generique des demandes explicites de categorie.

Interpretation architecturale :

- Cette extraction isole la frontiere sensible entre contexte/memoire, plan LLM et contraintes mecaniques du catalogue.
- Le code verifie que le scope est connu, stable et injectable dans les outils ; il ne decide pas qu'une source repond a la question utilisateur.
- Le LLM garde la decision semantique dans le planner, l'evidence judge et le writer.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.cs` est maintenant a 1 061 lignes.
- `ToolAgentOrchestrator.RagCategoryScopeResolution.cs` fait 44 lignes.
- `ToolAgentOrchestrator.SourceBackedInitialLlmCategoryScope.cs` fait 292 lignes.
- `ToolAgentOrchestrator.RagCategoryScopeLexical.cs` fait 123 lignes.
- `ToolAgentOrchestrator.DocumentaryRagDefaults.cs` fait 552 lignes et reste a decouper.
- Le garde-fou du fichier principal est abaisse a 1 250 lignes.
- Les nouveaux fichiers sont ajoutes aux garde-fous d'architecture.
- Le test d'architecture verifie que `ResolveRagCategoryScope`, `TryApplyInitialLlmSourceBackedCategoryScopeArgAsync` et `TryResolveLexicalRagCategoryScope` ne reviennent pas dans `ToolAgentOrchestrator.cs`.

Validation disponible apres extraction :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire les helpers de retrieval precis encore dans `ToolAgentOrchestrator.cs` :
  - `RagResultContainsUsableRequestedTitle` ;
  - `RagResultContainsExplicitDocumentHit` ;
  - `BuildPreciseRetrievalQueries` ;
  - `ExtractDelimitedNonFileTopics` ;
  - `QuoteLookupTitle` ;
  - `LooksLikeItemLocationLookupRequest`.
- Decouper ensuite `DocumentaryRagDefaults.cs`, qui reste le fichier extrait le plus dense.
- Continuer a rapprocher le pipeline d'un flux canonique unique `planner -> tools -> EvidenceBundle -> judge -> writer -> verifier -> repair -> UI`.

## Mise a jour - 2026-07-10 - extraction SourceBackedPreciseRetrieval

Extraction realisee :

- Les helpers de retrieval precis ont ete sortis de `ToolAgentOrchestrator.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.SourceBackedPreciseRetrieval.cs`.

Responsabilites extraites :

- Verification mecanique qu'un resultat RAG contient le titre demande.
- Verification mecanique qu'un resultat RAG contient le document explicite demande.
- Construction de variantes de requetes precises autour d'un titre exact.
- Extraction de sujets cites dans une question, en excluant les noms de fichiers.
- Mise entre guillemets d'un titre de lookup.
- Detection generique des demandes du type "dans quel document/source se trouve cet item".

Interpretation architecturale :

- Ce bloc reste un helper de retrieval et de contrat mecanique.
- Il ne decide pas si la source repond semantiquement a la question utilisateur.
- Il sert a mieux alimenter les outils RAG et a verifier des identites documentaires explicites.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.cs` est maintenant a 1 085 lignes.
- `ToolAgentOrchestrator.SourceBackedPreciseRetrieval.cs` fait 149 lignes.
- `ToolAgentOrchestrator.DocumentaryRagDefaults.cs` fait 552 lignes et reste le fichier extrait le plus dense.
- Le garde-fou du fichier principal est abaisse a 1 100 lignes.
- Le nouveau fichier est ajoute aux garde-fous d'architecture.
- Le test d'architecture verifie que les helpers de retrieval precis ne reviennent pas dans `ToolAgentOrchestrator.cs`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Decouper `DocumentaryRagDefaults.cs` en blocs plus lisibles.
- Puis extraire les derniers helpers generiques encore presents dans `ToolAgentOrchestrator.cs` afin de transformer le fichier principal en vrai noyau minimal.

## Mise a jour - 2026-07-10 - decoupage DocumentaryRagDefaults

Extraction realisee :

- Le fichier `ToolAgentOrchestrator.DocumentaryRagDefaults.cs` ne porte plus le gros bloc de 552 lignes.
- Le flow est decoupe en cinq responsabilites courtes :
  - `ToolAgentOrchestrator.DocumentaryRagDefaults.cs` ;
  - `ToolAgentOrchestrator.DocumentaryRagDefaultsContext.cs` ;
  - `ToolAgentOrchestrator.DocumentaryRagDefaultsMissingCall.cs` ;
  - `ToolAgentOrchestrator.DocumentaryRagSearchDefaults.cs` ;
  - `ToolAgentOrchestrator.DocumentaryRagMultiSearchDefaults.cs`.

Responsabilites separees :

- `DocumentaryRagDefaults` :
  - coordination courte du flow ;
  - skip trace si le router LLM a choisi general/no tools ;
  - injection d'un call RAG si le LLM n'en a pas prevu ;
  - delegation vers la normalisation `rag.search` ou `rag.multi_search`.
- `DocumentaryRagDefaultsContext` :
  - detection generique des formes de demande documentaire ;
  - calcul du titre exact, du mode broad, du topK initial et du `categoryScope`.
- `DocumentaryRagDefaultsMissingCall` :
  - creation du call RAG par defaut quand le router LLM n'a pas fourni de recherche.
- `DocumentaryRagSearchDefaults` :
  - normalisation des plans `rag.search` existants.
- `DocumentaryRagMultiSearchDefaults` :
  - normalisation des plans `rag.multi_search` existants.

Interpretation architecturale :

- Le code reste dans son role de normalisation d'outil, de contrat et de budget.
- La separation rend visible ce qui etait auparavant un gros bloc difficile a auditer.
- Aucune decision semantique de pertinence source n'a ete ajoutee au code.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.DocumentaryRagDefaults.cs` fait 60 lignes.
- `ToolAgentOrchestrator.DocumentaryRagDefaultsContext.cs` fait 84 lignes.
- `ToolAgentOrchestrator.DocumentaryRagDefaultsMissingCall.cs` fait 115 lignes.
- `ToolAgentOrchestrator.DocumentaryRagSearchDefaults.cs` fait 192 lignes.
- `ToolAgentOrchestrator.DocumentaryRagMultiSearchDefaults.cs` fait 186 lignes.
- `ToolAgentOrchestrator.cs` reste a 1 085 lignes.
- Les garde-fous d'architecture limitent ces fichiers a 90/120/140/220/220 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Continuer l'extraction des helpers encore presents dans `ToolAgentOrchestrator.cs`.
- Identifier les blocs generiques restants autour des arguments, de la serialization/tail prompt, de la garde document specifique et des reparations JSON.

## Mise a jour - 2026-07-10 - extraction JsonObjectRepair

Extraction realisee :

- Le parsing et la reparation JSON ont ete sortis de `ToolAgentOrchestrator.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.JsonObjectRepair.cs`.

Responsabilites extraites :

- Extraction du premier objet JSON dans une reponse brute.
- Reparation mecanique de delimiters JSON incomplets ou mal imbriques.
- Suppression des virgules finales avant fermetures JSON.
- Validation mecanique qu'une chaine parse bien en objet JSON.
- Extraction best-effort de `finalAnswer` depuis une sortie brute.
- Message d'erreur d'enveloppe JSON.

Interpretation architecturale :

- Ce bloc est purement mecanique et transversal.
- Il ne porte aucune logique metier ni decision semantique.
- Il renforce la separation entre orchestration LLM et verification/normalisation de contrat.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.cs` est maintenant a 841 lignes.
- `ToolAgentOrchestrator.JsonObjectRepair.cs` fait 250 lignes.
- Le garde-fou du fichier principal est abaisse a 860 lignes.
- Le nouveau fichier est ajoute aux garde-fous d'architecture avec une limite de 280 lignes.
- Le test d'architecture verifie que les helpers JSON ne reviennent pas dans `ToolAgentOrchestrator.cs`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire les helpers de garde document specifique et les helpers d'arguments RAG encore presents dans `ToolAgentOrchestrator.cs`.
- Continuer a reduire le fichier principal sans changer le contrat LLM/code.

## Mise a jour - 2026-07-10 - extraction SpecificDocumentQueryGuard

Extraction realisee :

- La garde de requete document specifique a ete sortie de `ToolAgentOrchestrator.cs`.
- Nouveau fichier :
  - `ToolAgentOrchestrator.SpecificDocumentQueryGuard.cs`.

Responsabilites extraites :

- Detection mecanique d'une requete qui pointe vers un document/PDF/fichier explicite.
- Filtrage mecanique des items RAG par identite documentaire explicite.
- Matching entre requete, `docName`, `docPath`, nom de fichier et variantes normalisees.

Interpretation architecturale :

- Ce bloc ne juge pas si le contenu du document repond a la question.
- Il limite seulement le resultat a un document explicitement nomme par l'utilisateur.
- La decision semantique reste dans le LLM planner/evidence judge/writer.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.cs` est maintenant a 740 lignes.
- `ToolAgentOrchestrator.SpecificDocumentQueryGuard.cs` fait 112 lignes.
- Le garde-fou du fichier principal est abaisse a 760 lignes.
- Le nouveau fichier est ajoute aux garde-fous d'architecture avec une limite de 140 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire les helpers d'arguments RAG et de serialisation encore presents dans `ToolAgentOrchestrator.cs`.
- Continuer a faire du fichier principal un noyau minimal : constructeur, etat essentiel, delegation.

## Mise a jour - 2026-07-10 - extraction RagArgumentHelpers et PromptSerialization

Extraction realisee :

- Les helpers d'arguments RAG ont ete sortis de `ToolAgentOrchestrator.cs`.
- Les helpers de serialisation/troncature pour prompts ont ete sortis de `ToolAgentOrchestrator.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.RagArgumentHelpers.cs` ;
  - `ToolAgentOrchestrator.PromptSerialization.cs`.

Responsabilites extraites :

- Lecture defensive des arguments string/int/tableau depuis `JsonElement`.
- Normalisation des requetes `rag.multi_search`.
- Deduplication mecanique des requetes RAG.
- Detection des lookups entre guillemets.
- Serialisation compacte des tool results pour les prompts.
- Serialisation de l'historique recent.
- Troncature defensive des chaines injectees dans les prompts/traces.

Interpretation architecturale :

- Ces blocs sont transversaux et mecaniques.
- Ils exposent des outils fiables aux autres partials sans ajouter de jugement semantique.
- La normalisation des requetes ne decide pas quelles sources repondent a la question.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.cs` est maintenant a 606 lignes.
- `ToolAgentOrchestrator.RagArgumentHelpers.cs` fait 109 lignes.
- `ToolAgentOrchestrator.PromptSerialization.cs` fait 44 lignes.
- Le garde-fou du fichier principal est abaisse a 620 lignes.
- Les nouveaux fichiers sont ajoutes aux garde-fous d'architecture avec limites 130 et 70 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagContextBudgetRegressionTests.SerializeTail_compacts_long_messages_before_writer_prompt" --verbosity:normal`
  - OK, 1/1.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire les helpers de fallback source-backed et d'erreurs d'outils encore presents dans `ToolAgentOrchestrator.cs`.
- Garder le fichier principal comme point d'assemblage minimal.

## Mise a jour - 2026-07-10 - extraction safe fallbacks et erreurs outils

Extraction realisee :

- Les fallbacks source-backed de securite ont ete sortis de `ToolAgentOrchestrator.cs`.
- La classification et le rendu des erreurs d'outils ont ete sortis de `ToolAgentOrchestrator.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.SourceBackedSafeFallbackAnswers.cs` ;
  - `ToolAgentOrchestrator.ToolExecutionErrors.cs`.

Responsabilites extraites :

- Construction d'un fallback source-backed qui evite les reponses brutes quand la preuve est insuffisante.
- Detection d'un fallback source-backed trop pauvre apres writer rejete.
- Gestion de l'insuffisance apres confirmation d'elargissement de recherche.
- Classification mecanique des erreurs d'outils/admin.
- Rendu deterministe des erreurs d'outils quand tous les tools echouent.

Interpretation architecturale :

- Les fallbacks restent des chemins de securite, pas le coeur souhaite du pipeline.
- Cette extraction les rend visibles et isoles, ce qui facilitera leur reduction ou leur remplacement par la boucle LLM/verifier/repair.
- Le code ne juge toujours pas la pertinence finale d'une source.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.cs` est maintenant a 391 lignes.
- `ToolAgentOrchestrator.SourceBackedSafeFallbackAnswers.cs` fait 129 lignes.
- `ToolAgentOrchestrator.ToolExecutionErrors.cs` fait 103 lignes.
- Le garde-fou du fichier principal est abaisse a 410 lignes.
- Les nouveaux fichiers sont ajoutes aux garde-fous d'architecture avec limites 150 et 130 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolExecutionErrorClassificationTests|FullyQualifiedName~ToolFailureDeterminismTests" --verbosity:normal`
  - OK, 7/7.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagContextBudgetRegressionTests.SerializeTail_compacts_long_messages_before_writer_prompt" --verbosity:normal`
  - OK, 1/1.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire la finalisation de tour et les helpers inventaire encore presents dans `ToolAgentOrchestrator.cs`.
- Auditer ensuite les gros fichiers extraits restants, notamment `SourceBackedFallbackAnswers.cs`.

## Mise a jour - 2026-07-10 - extraction TurnFinalization et InventoryHelpers

Extraction realisee :

- La finalisation de tour a ete sortie de `ToolAgentOrchestrator.cs`.
- Les helpers inventaire ont ete sortis de `ToolAgentOrchestrator.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.TurnFinalization.cs` ;
  - `ToolAgentOrchestrator.InventoryHelpers.cs`.

Responsabilites extraites :

- Nettoyage/maintien de clarification pending en fin de tour.
- Mise a jour de l'etat memoire du dernier tour.
- Emission de la trace `turn.final`.
- Log de fin de tour.
- Detection des intents inventaire.
- Detection des outils inventaire.
- Fallback deterministic inventory.
- Extraction du JSON inventory rendered.

Interpretation architecturale :

- La finalisation est maintenant isolee comme etape terminale du pipeline.
- Les helpers inventaire ne polluent plus le fichier principal.
- Le main orchestrator devient vraiment un point d'assemblage.
- Aucun hardcoding metier ou categorie n'a ete ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.cs` est maintenant a 228 lignes.
- `ToolAgentOrchestrator.TurnFinalization.cs` fait 80 lignes.
- `ToolAgentOrchestrator.InventoryHelpers.cs` fait 100 lignes.
- Le garde-fou du fichier principal est abaisse a 240 lignes.
- Les nouveaux fichiers sont ajoutes aux garde-fous d'architecture avec limites 100 et 120 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire les derniers helpers policy/router du fichier principal.
- Puis auditer les gros partials restants pour continuer le nettoyage structurel au-dela du main.

## Mise a jour - 2026-07-10 - extraction ActionRequest, RouterPlanHelpers et ToolPhaseLabels

Extraction realisee :

- Les derniers helpers encore presents dans `ToolAgentOrchestrator.cs` ont ete sortis.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.SourceBackedActionRequestClassification.cs` ;
  - `ToolAgentOrchestrator.RouterPlanHelpers.cs` ;
  - `ToolAgentOrchestrator.ToolPhaseLabels.cs`.

Responsabilites extraites :

- Detection generique d'une demande documentaire/source-backed.
- Normalisation du mode de plan router.
- Normalisation du format de reponse router.
- Clamp de confiance router.
- Inference d'intent a partir des tool calls.
- Detection des intents admin-only.
- Detection des outils grounded pour critic.
- Detection des formats de reponse "about".
- Libelle de phase UI pour les outils.

Interpretation architecturale :

- `ToolAgentOrchestrator.cs` ne porte plus de logique de classification ou policy.
- Le fichier principal est maintenant limite a l'etat, aux constantes et au constructeur.
- Les politiques router sont separees du noyau d'orchestration.
- La detection source-backed reste generique et multi-langue ; aucun mot-cle metier/cuisine n'a ete ajoute.
- Aucun lancement UI/backend/client n'a ete effectue.

Etat structurel courant :

- `ToolAgentOrchestrator.cs` est maintenant a 94 lignes.
- `ToolAgentOrchestrator.SourceBackedActionRequestClassification.cs` fait 47 lignes.
- `ToolAgentOrchestrator.RouterPlanHelpers.cs` fait 86 lignes.
- `ToolAgentOrchestrator.ToolPhaseLabels.cs` fait 9 lignes.
- Le garde-fou du fichier principal est abaisse a 120 lignes.
- Les fichiers extraits recents sont maintenant listes dans le test d'architecture avec des limites de taille explicites.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 23/23.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 41/41.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Probleme rencontre :

- Le premier build a depasse 120 secondes et a laisse deux processus `dotnet` appartenant bien a ce build.
- Ces processus ont ete arretes, puis le build a ete relance avec une fenetre plus longue.
- Le second build a reussi.

Prochaine cible :

- Auditer les gros partials restants, notamment `SourceBackedFallbackAnswers.cs`, `InventoryRendering.cs` et `RunPipeline.cs`.
- Continuer a reduire les fichiers qui portent encore plusieurs responsabilites.
- Avancer ensuite vers le flux canonique `EvidenceBundle -> judge -> writer -> verifier -> repair -> UI`, puis validation UI reelle.

## Mise a jour - 2026-07-10 - split du pipeline canonique SourceBackedRagPipeline

Extraction realisee :

- `SourceBackedRagPipeline.cs` a ete transforme en classe `partial`.
- Les etapes internes du pipeline canonique ont ete separees.
- Nouveaux fichiers :
  - `SourceBackedRagPipeline.LlmSteps.cs` ;
  - `SourceBackedRagPipeline.WriteVerifyRepair.cs` ;
  - `SourceBackedRagPipeline.ResultFactory.cs`.

Responsabilites separees :

- `SourceBackedRagPipeline.cs` :
  - orchestration globale ;
  - planner ;
  - execution initiale ;
  - boucle evidence judge ;
  - demandes follow-up ;
  - accumulation des resultats.
- `SourceBackedRagPipeline.LlmSteps.cs` :
  - appel LLM evidence judge ;
  - appel LLM writer ;
  - appel LLM repair.
- `SourceBackedRagPipeline.WriteVerifyRepair.cs` :
  - writer draft ;
  - verifier mecanique `SourceContractVerifier` ;
  - repair LLM cible ;
  - verification apres repair.
- `SourceBackedRagPipeline.ResultFactory.cs` :
  - construction du `SourceBackedPipelineResult`.

Interpretation architecturale :

- Le nouveau pipeline canonique est maintenant plus lisible et conforme aux etapes decidees :
  - planner ;
  - retrieval tools ;
  - EvidenceBundle ;
  - evidence judge ;
  - writer ;
  - source verifier ;
  - repair.
- Les traces restent presentes sur les etapes importantes :
  - planner ;
  - retrieval tools ;
  - EvidenceBundle ;
  - evidence judge ;
  - iteration controller ;
  - writer ;
  - source verifier ;
  - repair.
- Cette extraction ne rajoute aucun hardcoding metier.
- Aucun lancement UI/backend/client n'a ete effectue.

Etat structurel courant :

- `SourceBackedRagPipeline.cs` est maintenant a 231 lignes.
- `SourceBackedRagPipeline.LlmSteps.cs` fait 41 lignes.
- `SourceBackedRagPipeline.WriteVerifyRepair.cs` fait 65 lignes.
- `SourceBackedRagPipeline.ResultFactory.cs` fait 26 lignes.
- Un nouveau test d'architecture verrouille explicitement :
  - les limites de taille de ces fichiers ;
  - l'absence de `WriteAsync`, `RepairAsync` et `SourceContractVerifier.Verify` dans le runner principal ;
  - la presence des appels LLM dans `LlmSteps` ;
  - la presence du verifier mecanique dans `WriteVerifyRepair`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagPipelineTests" --verbosity:normal`
  - OK, 10/10.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagIterationTests" --verbosity:normal`
  - OK, 1/1.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 42/42.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Continuer le nettoyage des gros fichiers historiques encore actifs.
- Priorite proposee :
  - `SourceBackedFallbackAnswers.cs` ;
  - `InventoryRendering.cs` ;
  - `RunPipeline.cs`.
- Continuer a rapprocher le chemin actif du pipeline canonique plutot que de laisser l'ancien orchestrateur decider ou contourner trop de choses.

## Mise a jour - 2026-07-10 - extraction de helpers partages hors SourceBackedFallbackAnswers

Extraction realisee :

- Trois responsabilites partagees ont ete sorties de `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs`.
- Nouveaux fichiers :
  - `ToolAgentOrchestrator.SourceBackedRagHitPageKeys.cs` ;
  - `ToolAgentOrchestrator.SourceBackedPartialPlanningEvidenceLead.cs` ;
  - `ToolAgentOrchestrator.SourceBackedSoftChoiceHitMatching.cs`.

Responsabilites extraites :

- Calcul d'une cle visible de page RAG pour deduplication/source grouping.
- Record interne `PartialPlanningEvidenceLead`.
- Matching/contradiction soft-choice entre les termes demandes et les titres/evidences de hits.

Interpretation architecturale :

- Ces helpers sont utilises par plusieurs partials source-backed.
- Ils ne devaient pas rester caches dans `SourceBackedFallbackAnswers.cs`.
- L'extraction reduit un peu le fichier de fallback, mais ce fichier reste encore trop gros.
- Aucun hardcoding metier ou categorie n'a ete ajoute.
- Aucun lancement UI/backend/client n'a ete effectue.

Etat structurel courant :

- `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs` passe de 1428 a 1338 lignes.
- `ToolAgentOrchestrator.SourceBackedRagHitPageKeys.cs` fait 27 lignes.
- `ToolAgentOrchestrator.SourceBackedPartialPlanningEvidenceLead.cs` fait 12 lignes.
- `ToolAgentOrchestrator.SourceBackedSoftChoiceHitMatching.cs` fait 64 lignes.
- Les nouveaux fichiers sont ajoutes au test d'architecture source-backed avec limites explicites.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Tests exploratoires lances et resultat :

- Les suites completes `RagContextBudgetRegressionTests` et `StructuredPlanningCoverageTests` ont ete lancees comme verification large.
- Elles ont depasse 300 secondes et contiennent de nombreux tests rouges non limites a l'extraction.
- Les processus `dotnet` restants ont ete arretes proprement.
- Des tests cibles de fallback/candidate rendering ont ensuite ete lances :
  - `codex-test-targeted-readable-candidates.out` : 2/3 OK, 1 rouge.
  - `codex-test-targeted-structured-readable-fallbacks.out` : 2/3 OK, 1 rouge.
  - `codex-test-targeted-partial-planning-pagekeys.out` : 1/2 OK, 1 rouge.

Lecture des echecs :

- Les echecs pointent vers les anciens fallbacks deterministes :
  - titres issus de cartes sans evidence suffisante qui remontent encore ;
  - fallback lisible qui emet encore une liste alors que le test attend une absence de sortie ;
  - deduplication/qualification de fallback partiel qui ne produit pas l'insuffisance attendue.
- Comme l'extraction etait un deplacement mecanique de fonctions, ces rouges doivent etre traites comme dette comportementale exposee par les tests, pas comme preuve que le code compile mal.
- Cela confirme le risque deja identifie : les fallbacks deterministes historiques gardent encore trop de pouvoir de decision.

Prochaine cible :

- Ne pas reparer ces fallbacks en ajoutant une nouvelle couche d'heuristiques metier.
- Auditer comment remplacer ou contourner ces fallbacks par le pipeline canonique `EvidenceBundle -> evidence judge -> writer -> verifier -> repair`.
- Continuer ensuite le split de `SourceBackedFallbackAnswers.cs`, mais avec l'objectif de reduire son role actif plutot que d'en faire un meilleur decideur deterministe.

## Mise a jour - 2026-07-10 - suppression du rebuild RAG brut post-writer dans RunPipeline

Changement realise :

- Le chemin post-writer de `ToolAgentOrchestrator.RunPipeline.cs` ne reconstruit plus une reponse via `BuildRagEvidenceFallbackAnswer(...)` quand le writer rend une reponse de type "pas assez de donnees".
- A la place, le code :
  - emet la trace `writer.no_rag_data.raw_fallback.skipped` ;
  - documente la raison `canonical_writer_required_no_raw_deterministic_rebuild` ;
  - bascule vers `BuildSourceBackedSafeFallbackAnswer(..., shouldAvoidRaw: true)`.

Interpretation architecturale :

- C'est une reduction effective du pouvoir des anciens fallbacks deterministes.
- Le code ne tente plus de reconstruire une synthese source-backed brute apres un writer insuffisant sur ce chemin.
- Le comportement restant est un secours mecanique/safe fallback, pas un nouveau decideur semantique.
- Le chemin canonique garde la priorite :
  - pipeline source-backed avant post-tool fallback ;
  - writer/repair LLM ;
  - verifier/source guard ;
  - refus ou fallback safe quand le writer ne fournit pas une reponse supportee.
- Aucun hardcoding metier ou categorie n'a ete ajoute.
- Aucun lancement UI/backend/client n'a ete effectue.

Garde-fou ajoute :

- Le test d'architecture verifie maintenant que `ToolAgentOrchestrator.RunPipeline.cs` :
  - ne contient plus `BuildRagEvidenceFallbackAnswer(` ;
  - contient la trace `writer.no_rag_data.raw_fallback.skipped` ;
  - contient la raison `canonical_writer_required_no_raw_deterministic_rebuild`.

Etat structurel courant :

- `ToolAgentOrchestrator.RunPipeline.cs` fait 1064 lignes.
- `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs` reste a 1338 lignes.
- Les plus gros fichiers actifs restent a traiter :
  - `ToolAgentOrchestrator.ReplayAndExec.cs` : 3008 lignes ;
  - `ToolAgentOrchestrator.Shortcuts.cs` : 2311 lignes ;
  - `ToolAgentOrchestrator.TestHooks.cs` : 1808 lignes ;
  - `ToolAgentOrchestrator.LiveSummary.cs` : 1765 lignes ;
  - `ToolAgentOrchestrator.SourceBackedPlanningDraft.cs` : 1531 lignes ;
  - `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs` : 1338 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 42/42.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Continuer a cartographier et reduire les appels restants a `BuildRagEvidenceFallbackAnswer(...)`.
- Candidats encore actifs :
  - `SourceBackedExtractiveAnswerCore.cs` ;
  - `SourceBackedFallbackRouting.cs` ;
  - `SourceBackedSafeFallbackAnswers.cs`.
- Objectif : que ces chemins deviennent soit des refus/clarifications mecaniques, soit des appels au pipeline canonique, pas des syntheses deterministes concurrentes.

## Mise a jour - 2026-07-10 - SafeFallback ne rappelle plus le fallback RAG brut

Changement realise :

- `ToolAgentOrchestrator.SourceBackedSafeFallbackAnswers.cs` ne peut plus appeler `BuildRagEvidenceFallbackAnswer(...)`.
- La methode morte `BuildNonPoorSourceBackedFallbackAnswer(...)` a ete supprimee.
- `BuildSourceBackedSafeFallbackAnswer(...)` garde uniquement :
  - les refus/insuffisances source-backed ;
  - les fallbacks lisibles deja controles ;
  - `BuildBroadEvidenceStillInsufficientAnswer(...)`.

Interpretation architecturale :

- Le nom `SafeFallback` correspond mieux au comportement reel.
- Il n'existe plus de porte arriere depuis ce helper vers une synthese RAG brute.
- Cela reduit encore le role actif des anciens chemins deterministes.
- Aucun hardcoding metier ou categorie n'a ete ajoute.
- Aucun lancement UI/backend/client n'a ete effectue.

Garde-fou ajoute :

- Le test d'architecture verifie maintenant que `ToolAgentOrchestrator.SourceBackedSafeFallbackAnswers.cs` :
  - reste sous 130 lignes ;
  - ne contient pas `BuildRagEvidenceFallbackAnswer(` ;
  - ne contient pas `BuildNonPoorSourceBackedFallbackAnswer(`.

Etat structurel courant :

- `ToolAgentOrchestrator.SourceBackedSafeFallbackAnswers.cs` fait 117 lignes.
- Appels restants a `BuildRagEvidenceFallbackAnswer(...)` hors tests :
  - definition dans `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs` ;
  - appel dans `ToolAgentOrchestrator.SourceBackedExtractiveAnswerCore.cs` ;
  - appel dans `ToolAgentOrchestrator.SourceBackedFallbackRouting.cs`.
- `ToolAgentOrchestrator.RunPipeline.cs` ne contient toujours plus d'appel a `BuildRagEvidenceFallbackAnswer(...)`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 42/42.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Remplacer ou neutraliser les deux appels actifs restants :
  - `SourceBackedExtractiveAnswerCore.cs` ;
  - `SourceBackedFallbackRouting.cs`.
- Ensuite, envisager de conserver `BuildRagEvidenceFallbackAnswer(...)` uniquement derriere tests ou de l'archiver si plus aucun chemin actif n'en depend.

## Mise a jour - 2026-07-10 - plus aucun appel runtime actif au fallback RAG brut

Changement realise :

- Les deux derniers appels actifs a `BuildRagEvidenceFallbackAnswer(...)` ont ete neutralises.
- `ToolAgentOrchestrator.SourceBackedExtractiveAnswerCore.cs` :
  - quand aucun hit RAG exploitable n'existe, le code retourne maintenant `BuildBroadEvidenceStillInsufficientAnswer(...)`.
  - il ne tente plus une reconstruction brute.
- `ToolAgentOrchestrator.SourceBackedFallbackRouting.cs` :
  - le fallback de fragment documentaire ambigu utilise maintenant `BuildSourceBackedSafeFallbackAnswer(..., shouldAvoidRaw: true)`.
  - il ne passe plus par la synthese brute.

Interpretation architecturale :

- Il ne reste plus d'appel runtime actif a `BuildRagEvidenceFallbackAnswer(...)`.
- Les seules occurrences restantes sont :
  - la definition dans `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs` ;
  - le hook de test dans `ToolAgentOrchestrator.TestHooks.cs`.
- Les anciens chemins deterministes gardent donc moins de pouvoir.
- Les chemins actifs doivent maintenant passer par :
  - pipeline canonique ;
  - writer/repair ;
  - verifier/guards ;
  - refus ou fallback safe.
- Aucun hardcoding metier ou categorie n'a ete ajoute.
- Aucun lancement UI/backend/client n'a ete effectue.

Garde-fou ajoute :

- Le test d'architecture verifie maintenant qu'aucun fichier runtime `ToolAgentOrchestrator*.cs` ne rappelle `BuildRagEvidenceFallbackAnswer(...)`, sauf :
  - `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs` pour la definition ;
  - `ToolAgentOrchestrator.TestHooks.cs` pour les tests.

Etat structurel courant :

- `ToolAgentOrchestrator.RunPipeline.cs` fait 1064 lignes.
- `ToolAgentOrchestrator.SourceBackedSafeFallbackAnswers.cs` fait 117 lignes.
- `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs` fait 1338 lignes.
- Les plus gros fichiers actifs restent :
  - `ReplayAndExec.cs` : 3008 lignes ;
  - `Shortcuts.cs` : 2311 lignes ;
  - `TestHooks.cs` : 1808 lignes ;
  - `LiveSummary.cs` : 1765 lignes ;
  - `SourceBackedPlanningDraft.cs` : 1531 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, 0 warning, 0 erreur.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:normal`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:normal`
  - OK, 42/42.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:normal`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:normal`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:normal`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:normal`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- `git diff --check`
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Decider quoi faire de la definition `BuildRagEvidenceFallbackAnswer(...)` :
  - soit l'archiver dans `OLD` si les tests peuvent etre migres ;
  - soit la garder temporairement pour tests/reference, mais sans chemin runtime actif.
- Continuer ensuite le nettoyage des gros fichiers source-backed, en priorite `SourceBackedFallbackAnswers.cs` et `SourceBackedPlanningDraft.cs`.

## Mise a jour - 2026-07-10 - fallback RAG brut archive hors runtime actif

Changement realise :

- La definition active `BuildRagEvidenceFallbackAnswer(...)` a ete retiree de `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs`.
- L'ancien bloc a ete archive en reference seule dans `client/SAAIA.Client.WinUI/ToolAgent/OLD/ToolAgentOrchestrator.LegacyRawRagEvidenceFallbackAnswer.20260710.cs`.
- Le projet WinUI exclut deja `ToolAgent/OLD/**/*.cs` de la compilation, donc ce code n'est pas actif.
- Le hook de compatibilite `BuildRagEvidenceFallbackAnswerForTests(...)` existe encore pour les anciens tests, mais il redirige maintenant vers `BuildSourceBackedSafeFallbackAnswer(..., shouldAvoidRaw: true)`.
- Le garde-fou d'architecture a ete durci :
  - l'archive `OLD` doit exister ;
  - aucun fichier runtime actif `ToolAgentOrchestrator*.cs` ne doit contenir `BuildRagEvidenceFallbackAnswer(`.

Interpretation architecturale :

- Le vieux fallback brut n'est plus un chemin runtime, meme indirect.
- Le code actif ne peut plus reconstruire une reponse source-backed brute depuis quelques hits sans passer par les chemins safe/canoniques.
- Cela avance vers le principe demande : le code ne doit pas reprendre la decision semantique au LLM ; il doit seulement fournir des outils, contrats, traces, verifications et fallbacks prudents.
- Aucun hardcoding metier ou categorie n'a ete ajoute.
- Aucun lancement UI/backend/client n'a ete effectue.

Etat structurel courant :

- `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs` est passe de 1338 a 1165 lignes.
- Une limite d'architecture a ete ajoutee pour garder ce fichier sous 1200 lignes.
- Il reste encore trop gros et devra continuer a etre decoupe, mais il ne contient plus la methode brute archivee.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - premiere tentative timeout apres 184 s, deux processus `dotnet` du build ont ete arretes proprement ;
  - relance longue OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ToolRouterPlanNormalizationTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 94/94.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~RagQueryNormalizationRegressionTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 38/38.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedEvidencePlannerObservability" --verbosity:minimal /m:1 /nr:false`
  - OK, 35/35.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "source_policy_guard" --verbosity:minimal /m:1 /nr:false`
  - OK, 13/13.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check` :
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Continuer le decoupage de `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs`, qui reste a 1165 lignes.
- Ensuite poursuivre le nettoyage des gros fichiers source-backed actifs, notamment `SourceBackedPlanningDraft.cs`, en gardant le principe : pas de vieux chemin concurrent, pas de decision semantique deterministe, et des traces explicites par etape.

## Mise a jour - 2026-07-10 - decoupage de SourceBackedFallbackAnswers

Changement realise :

- `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs` a ete decoupe par responsabilite.
- Nouveaux fichiers actifs crees :
  - `ToolAgentOrchestrator.SourceBackedCategoryOverviewFallback.cs` : vue categorie generique.
  - `ToolAgentOrchestrator.SourceBackedBriefCitationFallback.cs` : reponse courte sourcee/citee.
  - `ToolAgentOrchestrator.SourceBackedInsufficientEvidenceFallbacks.cs` : insuffisance de preuves et suppression d'offre deja confirmee.
  - `ToolAgentOrchestrator.SourceBackedFallbackIntentPolicy.cs` : politique generique de fallback/intention.
  - `ToolAgentOrchestrator.SourceBackedStructuredPlanningPreWriterFallback.cs` : garde pre-writer pour planning structure.
- Aucun comportement volontairement change : extraction mecanique en partials courts.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.SourceBackedFallbackAnswers.cs` : 594 lignes.
- `ToolAgentOrchestrator.SourceBackedCategoryOverviewFallback.cs` : 83 lignes.
- `ToolAgentOrchestrator.SourceBackedBriefCitationFallback.cs` : 78 lignes.
- `ToolAgentOrchestrator.SourceBackedInsufficientEvidenceFallbacks.cs` : 134 lignes.
- `ToolAgentOrchestrator.SourceBackedFallbackIntentPolicy.cs` : 202 lignes.
- `ToolAgentOrchestrator.SourceBackedStructuredPlanningPreWriterFallback.cs` : 114 lignes.
- Le garde-fou d'architecture a ete mis a jour :
  - les nouveaux fichiers doivent rester courts ;
  - `SourceBackedFallbackAnswers.cs` est maintenant limite a 650 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check` :
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Continuer a reduire les gros fichiers actifs du chemin source-backed.
- Candidat prioritaire : `ToolAgentOrchestrator.SourceBackedPlanningDraft.cs`, encore autour de 1500 lignes, a decouper par generation de brouillon, verification des items, et rendu final.
- Garder la ligne d'architecture : le code structure, trace et verifie ; il ne remplace pas le jugement semantique du LLM.

## Mise a jour - 2026-07-10 - decoupage de SourceBackedPlanningDraft

Changement realise :

- `ToolAgentOrchestrator.SourceBackedPlanningDraft.cs` a ete reduit par extraction de trois responsabilites distinctes.
- Nouveaux fichiers actifs crees :
  - `ToolAgentOrchestrator.SourceBackedPlanningCandidateRanking.cs` : ranking, diversite page, score route/retrieval, score lisibilite.
  - `ToolAgentOrchestrator.SourceBackedPlanningCandidateRendering.cs` : rendu des candidats, titres affichables, open tokens.
  - `ToolAgentOrchestrator.SourceBackedPlanningWriterGates.cs` : decisions d'autorisation writer/repair/coverage pour planning source-backed.
- Le builder principal du draft reste dans `SourceBackedPlanningDraft.cs`.
- La grille structuree reste encore dans ce fichier et pourra etre extraite ensuite.
- Aucun comportement volontairement change : extraction mecanique en partials courts.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.SourceBackedPlanningDraft.cs` : 777 lignes.
- `ToolAgentOrchestrator.SourceBackedPlanningCandidateRanking.cs` : 220 lignes.
- `ToolAgentOrchestrator.SourceBackedPlanningCandidateRendering.cs` : 209 lignes.
- `ToolAgentOrchestrator.SourceBackedPlanningWriterGates.cs` : 352 lignes.
- Le garde-fou d'architecture a ete mis a jour :
  - `SourceBackedPlanningDraft.cs` est limite a 850 lignes ;
  - les trois nouveaux fichiers ont leurs propres limites.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check` :
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Extraire la grille structuree de `SourceBackedPlanningDraft.cs` vers un fichier dedie.
- Ensuite identifier les derniers gros fichiers actifs du chemin source-backed avant de passer a une validation plus large puis au test UI reel.

## Mise a jour - 2026-07-10 - extraction de la grille structuree

Changement realise :

- La grille structuree a ete extraite de `ToolAgentOrchestrator.SourceBackedPlanningDraft.cs`.
- Nouveau fichier actif cree :
  - `ToolAgentOrchestrator.SourceBackedStructuredPlanningGrid.cs`.
- Ce fichier contient :
  - construction de grille slot-aware ;
  - rotation/distinction des candidats ;
  - termes de slots ;
  - records `StructuredPlanningSlotTermGroup` et `StructuredPlanningSlotFitSummary` ;
  - verification stricte d'evidence candidat pour planning structure.
- Le fichier `SourceBackedPlanningDraft.cs` garde maintenant surtout le builder du draft et le rendu principal du plan.
- Aucun comportement volontairement change : extraction mecanique.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.SourceBackedPlanningDraft.cs` : 417 lignes.
- `ToolAgentOrchestrator.SourceBackedStructuredPlanningGrid.cs` : 368 lignes.
- Le garde-fou d'architecture a ete mis a jour :
  - `SourceBackedPlanningDraft.cs` limite a 450 lignes ;
  - `SourceBackedStructuredPlanningGrid.cs` limite a 400 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check` :
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Faire un inventaire des plus gros fichiers actifs restants du chemin `ToolAgent` source-backed.
- Prioriser les fichiers qui melangent encore orchestration, decision policy, rendu ou verification.
- Apres nettoyage structurel suffisant : relancer une validation plus large, puis preparer le test UI reel client.

## Mise a jour - 2026-07-10 - extraction des traces runtime de suffisance

Changement realise :

- Les traces runtime planning ont ete extraites de `ToolAgentOrchestrator.SourceBackedEvidenceSufficiency.cs`.
- Nouveau fichier actif cree :
  - `ToolAgentOrchestrator.SourceBackedPlanningRuntimeTrace.cs`.
- Ce fichier contient :
  - construction des lignes de trace planning ;
  - diagnostics de candidat source-backed ;
  - emission `ClientLog` et `EmitRagTrace` ;
  - append des requetes de trace.
- `SourceBackedEvidenceSufficiency.cs` garde maintenant l'analyse de suffisance, la couverture planning et les helpers de materiel/source.
- Aucun evenement de trace n'a ete renomme.
- Aucun comportement volontairement change : extraction mecanique.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.SourceBackedEvidenceSufficiency.cs` : 811 lignes.
- `ToolAgentOrchestrator.SourceBackedPlanningRuntimeTrace.cs` : 481 lignes.
- Le garde-fou d'architecture a ete mis a jour :
  - `SourceBackedEvidenceSufficiency.cs` limite a 850 lignes ;
  - `SourceBackedPlanningRuntimeTrace.cs` limite a 520 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check` :
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Continuer l'inventaire des gros fichiers actifs.
- Les prochains candidats probables sont `SourceBackedRagJsonNormalization.cs`, `InventoryRendering.cs`, `SourceBackedPlanningFieldValueCandidates.cs`, `RunPipeline.cs` et les gros fichiers de nettoyage/rendu source-backed.

## Mise a jour - 2026-07-10 - decoupage de SourceBackedRagJsonNormalization

Changement realise :

- `ToolAgentOrchestrator.SourceBackedRagJsonNormalization.cs` a ete decoupe par responsabilite.
- Nouveaux fichiers actifs crees :
  - `ToolAgentOrchestrator.RagQueryTextNormalization.cs` : normalisation texte des requetes RAG, enveloppes de recherche elargie, nettoyage topic.
  - `ToolAgentOrchestrator.SourceBackedExtractionDiagnosticsRefs.cs` : diagnostics extraction/chunk quality et compaction associee.
  - `ToolAgentOrchestrator.SourceBackedContextualSnippetCompaction.cs` : compaction des snippets contextuels et decisions previous/next evidence.
  - `ToolAgentOrchestrator.JsonElementAccessors.cs` : accesseurs `JsonElement` generiques.
- `SourceBackedRagJsonNormalization.cs` garde maintenant la normalisation des hits RAG et les compactions de payload source-backed.
- Une erreur d'extraction initiale a ete corrigee :
  - deux helpers expression-bodied contenant la regex `@"[,;]"` avaient ete coupes sur le point-virgule de la chaine ;
  - les deux helpers ont ete reconstruits dans `SourceBackedContextualSnippetCompaction.cs` et les fragments residuels ont ete retires.
- Aucun comportement volontairement change.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.SourceBackedRagJsonNormalization.cs` : 424 lignes.
- `ToolAgentOrchestrator.RagQueryTextNormalization.cs` : 201 lignes.
- `ToolAgentOrchestrator.SourceBackedExtractionDiagnosticsRefs.cs` : 332 lignes.
- `ToolAgentOrchestrator.SourceBackedContextualSnippetCompaction.cs` : 158 lignes.
- `ToolAgentOrchestrator.JsonElementAccessors.cs` : 134 lignes.
- Le garde-fou d'architecture a ete mis a jour avec des limites pour ces fichiers.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - premiere relance apres extraction : echec compile detecte et corrige ;
  - relance finale OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check` :
  - aucune erreur bloquante ;
  - avertissements CRLF/LF encore presents dans le worktree.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Continuer le nettoyage des gros fichiers actifs restants.
- Candidats restants prioritaires : `InventoryRendering.cs`, `SourceBackedPlanningFieldValueCandidates.cs`, `RunPipeline.cs`, `SourceBackedPlanningTitleQuality.cs`, `SourceBackedOptionTitleCleaning.cs`.

## Mise a jour - 2026-07-10 - decoupage des candidats FieldValue

Changement realise :

- `ToolAgentOrchestrator.SourceBackedPlanningFieldValueCandidates.cs` a ete decoupe par responsabilite.
- Nouveaux fichiers actifs crees :
  - `ToolAgentOrchestrator.SourceBackedPlanningFieldValueRejectionKinds.cs` : formes mecaniques de rejet des candidats qui ressemblent a des valeurs de champ structurees.
  - `ToolAgentOrchestrator.SourceBackedPlanningFieldValueOwnership.cs` : verification locale d'appartenance d'un titre candidat a une section structuree, sans faire juger la pertinence semantique par le code.
- `SourceBackedPlanningFieldValueCandidates.cs` garde maintenant l'orchestration de rejet FieldValue et les helpers de preuve locale.
- Aucun comportement volontairement change : extraction mecanique.
- Aucun hardcoding metier ou categorie ajoute.
- La logique reste generique : elle porte sur des formes structurees, listes, champs, titres et preuves locales, pas sur une categorie documentaire particuliere.

Etat structurel courant :

- `ToolAgentOrchestrator.SourceBackedPlanningFieldValueCandidates.cs` : 305 lignes.
- `ToolAgentOrchestrator.SourceBackedPlanningFieldValueRejectionKinds.cs` : 343 lignes.
- `ToolAgentOrchestrator.SourceBackedPlanningFieldValueOwnership.cs` : 475 lignes.
- Le garde-fou d'architecture a ete mis a jour :
  - `SourceBackedPlanningFieldValueCandidates.cs` limite a 420 lignes ;
  - `SourceBackedPlanningFieldValueRejectionKinds.cs` limite a 420 lignes ;
  - `SourceBackedPlanningFieldValueOwnership.cs` limite a 560 lignes.

Validation a executer apres cette mise a jour :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`

Prochaine cible :

- Ne pas decouper selon la taille seule.
- Inspecter les fichiers restants selon les responsabilites melangees : orchestration, execution outil, rendu UI, verification source, repair, traces, memoire.
- Garder les fichiers entre 1000 et 2000 lignes s'ils sont coherents et ne melangent pas plusieurs roles.

## Mise a jour - 2026-07-10 - decoupage qualitatif de Shortcuts

Changement realise :

- `ToolAgentOrchestrator.Shortcuts.cs` a ete decoupe selon responsabilites, pas selon un seuil de taille arbitraire.
- Deux blocs complets ont ete extraits sans couper de fonction :
  - `ToolAgentOrchestrator.ShortcutTranslation.cs` : traduction du dernier rendu assistant avec preservation des valeurs canoniques/documentaires.
  - `ToolAgentOrchestrator.DocumentContentSearchShortcut.cs` : detection et expansion des demandes de recherche de contenu documentaire, construction des variantes de requete et rendu des documents trouves.
- `Shortcuts.cs` reste le coordinateur des shortcuts deterministes et conserve une taille acceptable tant qu'il reste coherent.
- Aucun comportement volontairement change : extraction mecanique.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.Shortcuts.cs` : 1619 lignes.
- `ToolAgentOrchestrator.ShortcutTranslation.cs` : 209 lignes.
- `ToolAgentOrchestrator.DocumentContentSearchShortcut.cs` : 510 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.

Prochaine cible :

- Continuer avec la meme regle : ne pas reduire les fichiers pour reduire les fichiers.
- Candidats a inspecter qualitativement :
  - `InventoryRendering.cs` : semble melanger execution locale, update memoire, rendu de payloads inventory et guidance backend.
  - `Tools.cs` : semble melanger resolution de document, resolution categorie, parsing d'arguments outil et refs source.
  - `RunPipeline.cs` : contient encore l'orchestration principale ; a decouper seulement si une extraction garde les etapes lisibles sans casser la comprehension.

## Mise a jour - 2026-07-10 - decoupage qualitatif de InventoryRendering

Changement realise :

- `ToolAgentOrchestrator.InventoryRendering.cs` a ete decoupe selon responsabilites.
- Nouveaux fichiers actifs crees :
  - `ToolAgentOrchestrator.LocalMetaTools.cs` : execution locale du tool `meta.list_questions`.
  - `ToolAgentOrchestrator.InventoryTextAnswers.cs` : reponses texte deterministes pour `documents.list` et `documents.tree`.
  - `ToolAgentOrchestrator.BackendGuidanceExpansion.cs` : expansion RAG quand le backend renvoie une guidance de clarification.
  - `ToolAgentOrchestrator.InventoryMemoryState.cs` : mise a jour de la memoire documentaire depuis les resultats `documents.extraction_quality`.
- `InventoryRendering.cs` garde maintenant principalement la construction du payload canonique `inventory.rendered` et les builders de donnees inventory.
- Un import manquant a ete corrige apres build :
  - `BackendGuidanceExpansion.cs` avait besoin de `SAAIA.Client.WinUI.Localization` pour `DeterministicAgentText`.
- Les imports herites inutiles ont ete retires de `InventoryRendering.cs`.
- Aucun comportement volontairement change : extraction mecanique.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.InventoryRendering.cs` : 795 lignes.
- `ToolAgentOrchestrator.LocalMetaTools.cs` : 34 lignes.
- `ToolAgentOrchestrator.InventoryTextAnswers.cs` : 85 lignes.
- `ToolAgentOrchestrator.BackendGuidanceExpansion.cs` : 151 lignes.
- `ToolAgentOrchestrator.InventoryMemoryState.cs` : 110 lignes.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - premiere relance apres extraction : echec compile detecte et corrige ;
  - relance finale OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Inspecter `Tools.cs` avant tout decoupage : il peut probablement etre separe entre resolution document, resolution categorie, parsing d'arguments RAG et refs source.
- Ne pas toucher a `RunPipeline.cs` tant qu'une extraction evidente ne preserve pas mieux la lisibilite de l'orchestration.

## Mise a jour - 2026-07-10 - suppression du fourre-tout Tools.cs

Changement realise :

- `ToolAgentOrchestrator.Tools.cs` a ete supprime comme fichier fourre-tout actif.
- Son contenu a ete remplace par cinq fichiers nommes par responsabilite :
  - `ToolAgentOrchestrator.ToolReferenceModels.cs` : records/classes internes `ResolvedDocRef`, `SummaryChunk`, `ExplicitDocumentResolution`.
  - `ToolAgentOrchestrator.ToolArgumentAccessors.cs` : parsing des arguments JSON des tools, y compris arguments RAG.
  - `ToolAgentOrchestrator.ToolCategoryScopeResolution.cs` : resolution du scope categorie et references categorie connues.
  - `ToolAgentOrchestrator.DocumentReferenceResolution.cs` : resolution document, matching exact/fuzzy, focus document et variantes de recherche.
  - `ToolAgentOrchestrator.SourceReferenceResolution.cs` : resolution des refs source/PDF et construction de `SourceRef` depuis un document.
- Aucun comportement volontairement change : extraction mecanique.
- Cette extraction suit la regle utilisateur : separation par responsabilite, pas reduction artificielle de taille.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.ToolReferenceModels.cs` : 81 lignes.
- `ToolAgentOrchestrator.ToolArgumentAccessors.cs` : 161 lignes.
- `ToolAgentOrchestrator.ToolCategoryScopeResolution.cs` : 123 lignes.
- `ToolAgentOrchestrator.DocumentReferenceResolution.cs` : 753 lignes.
- `ToolAgentOrchestrator.SourceReferenceResolution.cs` : 148 lignes.
- `ToolAgentOrchestrator.Tools.cs` : supprime.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Refaire un inventaire des gros fichiers apres ces extractions.
- Prochaines cibles probables selon responsabilite reelle :
  - `ReplayAndExec.cs` : tres gros, probablement melange execution/replay/dispatch.
  - `LiveSummary.cs` : long mais peut rester acceptable si son domaine est coherent.
  - `RunPipeline.cs` : a traiter seulement avec un plan clair par etape d'orchestration.

## Mise a jour - 2026-07-10 - suppression du fourre-tout ReplayAndExec.cs

Changement realise :

- `ToolAgentOrchestrator.ReplayAndExec.cs` a ete supprime comme fichier fourre-tout actif.
- Son contenu a ete remplace par sept fichiers nommes par responsabilite :
  - `ToolAgentOrchestrator.RagMultiSearchSupport.cs` : constantes, records et helpers de scoring/dedupe pour RAG multi-search.
  - `ToolAgentOrchestrator.InventoryReplayRendering.cs` : replay du dernier rendu inventory et renderers deterministes inventory.
  - `ToolAgentOrchestrator.DocumentToolExecution.cs` : execution des tools documents list/search/get.
  - `ToolAgentOrchestrator.RagSearchExecution.cs` : execution `rag.search`, payload busy/timeout et helpers de scope RAG.
  - `ToolAgentOrchestrator.RagMultiSearchExecution.cs` : execution `rag.multi_search`, y compris la grosse methode imbriquee de probing categorie. Ce fichier reste volontairement long car la methode est coherentement imbriquee et ne doit pas etre coupee artificiellement.
  - `ToolAgentOrchestrator.RagDiagnostics.cs` : diagnostics RAG, degraded retrievers et labels de hits.
  - `ToolAgentOrchestrator.ExportSupportAndDebugTools.cs` : export, support bundle et `rag.debug.scroll`.
- Corrections post-extraction :
  - ajout de `System.Globalization` dans les fichiers qui utilisent `CultureInfo` ;
  - ajout de `System.Text.RegularExpressions` dans `RagSearchExecution.cs` ;
  - trace `rag.multi_search.category_probe.end` rendue explicite avec `string.Join(" | ", summary)` au lieu de passer un tableau dans le tuple ;
  - indentation de `ExecExportCreate`.
- Aucun comportement volontairement change : extraction mecanique.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.RagMultiSearchSupport.cs` : 311 lignes.
- `ToolAgentOrchestrator.InventoryReplayRendering.cs` : 648 lignes.
- `ToolAgentOrchestrator.DocumentToolExecution.cs` : 74 lignes.
- `ToolAgentOrchestrator.RagSearchExecution.cs` : 440 lignes.
- `ToolAgentOrchestrator.RagMultiSearchExecution.cs` : 1362 lignes.
- `ToolAgentOrchestrator.RagDiagnostics.cs` : 161 lignes.
- `ToolAgentOrchestrator.ExportSupportAndDebugTools.cs` : 83 lignes.
- `ToolAgentOrchestrator.ReplayAndExec.cs` : supprime.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - premiere relance apres extraction : echec compile mecanique detecte et corrige ;
  - relance finale OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Refaire un inventaire des gros fichiers restants.
- `RagMultiSearchExecution.cs` peut rester >1000 lignes tant que la grosse methode imbriquee reste lisible et coherentement locale.
- Inspecter ensuite `LiveSummary.cs` et `RunPipeline.cs`, mais ne decouper que si les responsabilites sont nettes.

## Mise a jour - 2026-07-10 - extraction de LiveSummary.cs par responsabilite

Contexte :

- La consigne utilisateur a ete clarifiee : ne pas reduire les tailles pour reduire les tailles.
- Les fichiers entre 1000 et 2000 lignes restent acceptables si la responsabilite est coherente.
- Le decoupage devient utile quand plusieurs fonctions/responsabilites differentes sont melangees dans un meme fichier.

Changement realise :

- `ToolAgentOrchestrator.LiveSummary.cs` a ete supprime comme fichier actif unique.
- Son contenu a ete remplace par six fichiers specialises, avec fonctions conservees entieres :
  - `ToolAgentOrchestrator.LiveSummaryExecution.cs` : execution `rag.summarize_live`, acces debug scroll, batching et health admin.
  - `ToolAgentOrchestrator.LiveSummarySourceMaterial.cs` : resolution metadata source, fallback metadata, extraction de chunks depuis debug scroll/RAG item, conversion de profile signals.
  - `ToolAgentOrchestrator.LiveSummarySelection.cs` : selection representative des chunks, scoring de selection et metadata de sampling.
  - `ToolAgentOrchestrator.LiveSummaryPromptFormatting.cs` : construction du prompt de resume, metadata source, content cards et compactage evidence.
  - `ToolAgentOrchestrator.LiveSummaryRetrievalQueries.cs` : construction des requetes de retrieval summary et termes derives des metadata/evidence/path/facts.
  - `ToolAgentOrchestrator.LiveSummaryAnchors.cs` : construction des ancres UI/source refs et payloads d'ancrage.
- Aucun comportement volontairement change : extraction mecanique par blocs de methodes.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.LiveSummaryExecution.cs` : 292 lignes.
- `ToolAgentOrchestrator.LiveSummarySourceMaterial.cs` : 304 lignes.
- `ToolAgentOrchestrator.LiveSummarySelection.cs` : 118 lignes.
- `ToolAgentOrchestrator.LiveSummaryPromptFormatting.cs` : 471 lignes.
- `ToolAgentOrchestrator.LiveSummaryRetrievalQueries.cs` : 472 lignes.
- `ToolAgentOrchestrator.LiveSummaryAnchors.cs` : 187 lignes.
- `ToolAgentOrchestrator.LiveSummary.cs` : supprime.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check` :
  - uniquement des avertissements CRLF/LF deja connus, pas d'erreur de whitespace bloquante.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Les plus gros fichiers actifs restants sont maintenant :
  - `ToolAgentOrchestrator.TestHooks.cs` : 1808 lignes.
  - `ToolAgentOrchestrator.Shortcuts.cs` : 1619 lignes.
  - `ToolAgentOrchestrator.RagMultiSearchExecution.cs` : 1362 lignes.
  - `ToolAgentOrchestrator.RunPipeline.cs` : 1064 lignes.
  - `ToolAgentOrchestrator.SourceBackedPlanningTitleQuality.cs` : 1037 lignes.
  - `ToolAgentOrchestrator.SourceBackedOptionTitleCleaning.cs` : 1018 lignes.
  - `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceability.cs` : 1008 lignes.
  - `ToolAgentOrchestrator.SourceBackedRetrievalQueries.cs` : 1002 lignes.
- Ne pas decouper automatiquement ces fichiers par taille.
- Inspecter d'abord `RunPipeline.cs` et/ou `TestHooks.cs` pour verifier s'ils melangent de vraies responsabilites differentes.

## Mise a jour - 2026-07-10 - extraction de Shortcuts.cs par responsabilite

Contexte :

- `RunPipeline.cs` a ete inspecte en premier : il contient essentiellement une seule grosse methode `RunAsync`.
- Conformement a la consigne, il n'a pas ete decoupe artificiellement.
- `ToolAgentOrchestrator.Shortcuts.cs` melangeait en revanche plusieurs responsabilites nettes :
  - execution du shortcut deterministe ;
  - capture d'etat conversationnel structure ;
  - snapshots categories/status/documents ;
  - detection d'intentions/follow-ups ;
  - matching et normalisation des prompts UI canoniques.

Changement realise :

- `ToolAgentOrchestrator.Shortcuts.cs` a ete supprime comme fichier actif unique.
- Il a ete remplace par quatre fichiers specialises :
  - `ToolAgentOrchestrator.ShortcutExecution.cs` : grosse methode `TryHandleDeterministicShortcutAsync`, conservee entiere.
  - `ToolAgentOrchestrator.ShortcutConversationState.cs` : capture memoire, snapshots categories/status, JSON canonique documents.
  - `ToolAgentOrchestrator.ShortcutIntentDetection.cs` : detection de courtoisie, follow-ups, commandes directes, commandes malformees et extractions associees.
  - `ToolAgentOrchestrator.ShortcutPromptMatching.cs` : matching des prompts canoniques, normalisation exacte et normalisation token shortcut.
- Correction mecanique post-extraction :
  - les vieux litteraux mojibake `char` dans `NormalizeExactPromptText` ont ete remplaces par des chaines/echappements Unicode valides.
  - La logique reste equivalente, avec prise en charge explicite des apostrophes/espaces/ellipsis Unicode normaux et mojibake.
- Aucun comportement volontairement change.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.ShortcutExecution.cs` : 543 lignes.
- `ToolAgentOrchestrator.ShortcutConversationState.cs` : 470 lignes.
- `ToolAgentOrchestrator.ShortcutIntentDetection.cs` : 505 lignes.
- `ToolAgentOrchestrator.ShortcutPromptMatching.cs` : 152 lignes.
- `ToolAgentOrchestrator.Shortcuts.cs` : supprime.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - premiere relance apres extraction : echec compile mecanique lie aux vieux litteraux mojibake ;
  - correction appliquee ;
  - relance finale OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check` :
  - uniquement des avertissements CRLF/LF deja connus, pas d'erreur de whitespace bloquante.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Les plus gros fichiers actifs restants sont maintenant :
  - `ToolAgentOrchestrator.TestHooks.cs` : 1808 lignes.
  - `ToolAgentOrchestrator.RagMultiSearchExecution.cs` : 1362 lignes.
  - `ToolAgentOrchestrator.RunPipeline.cs` : 1064 lignes.
  - `ToolAgentOrchestrator.SourceBackedPlanningTitleQuality.cs` : 1037 lignes.
  - `ToolAgentOrchestrator.SourceBackedOptionTitleCleaning.cs` : 1018 lignes.
  - `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceability.cs` : 1008 lignes.
  - `ToolAgentOrchestrator.SourceBackedRetrievalQueries.cs` : 1002 lignes.
- `RunPipeline.cs` et `RagMultiSearchExecution.cs` semblent longs mais potentiellement coherents.
- Inspecter `TestHooks.cs` pour voir s'il peut etre separe par familles de tests sans changer le comportement.

## Mise a jour - 2026-07-10 - extraction de TestHooks.cs par familles de hooks

Contexte :

- `ToolAgentOrchestrator.TestHooks.cs` etait le plus gros fichier actif restant.
- Meme s'il ne porte pas le pipeline runtime direct, il melangeait beaucoup de familles de hooks exposees aux tests.
- Le decoupage est utile car il aide a comprendre quelles surfaces du backend/orchestrateur sont protegees par les tests.

Changement realise :

- `ToolAgentOrchestrator.TestHooks.cs` a ete supprime comme fichier actif unique.
- Il a ete remplace par cinq fichiers `#if DEBUG`, separes par familles coherentes :
  - `ToolAgentOrchestrator.TestHooksCoreAndSourcePayloads.cs` : hooks de base router/langue/json, payloads sources, fixtures de sources dupliquees et hooks admin/source resolve.
  - `ToolAgentOrchestrator.TestHooksPlanning.cs` : hooks de planning source-backed structure, drafts, coverage et traces planning.
  - `ToolAgentOrchestrator.TestHooksExplorationAndRetrieval.cs` : hooks exploration evidence, LLM planner prompts/parsing, scope categorie, probes documentaires, budgets/topK/limites retrieval.
  - `ToolAgentOrchestrator.TestHooksWriterAndFallbacks.cs` : hooks writer, repair, adjudication, fallback safe/readable, policy guard, options et countdown planning.
  - `ToolAgentOrchestrator.TestHooksDocumentaryAndQuery.cs` : hooks query normalization, action/planning retrieval, precise/comparative retrieval, documentary probe et document content search.
- Les hooks ont ete conserves entiers.
- Aucun comportement volontairement change.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.TestHooksCoreAndSourcePayloads.cs` : 585 lignes.
- `ToolAgentOrchestrator.TestHooksExplorationAndRetrieval.cs` : 512 lignes.
- `ToolAgentOrchestrator.TestHooksWriterAndFallbacks.cs` : 301 lignes.
- `ToolAgentOrchestrator.TestHooksPlanning.cs` : 255 lignes.
- `ToolAgentOrchestrator.TestHooksDocumentaryAndQuery.cs` : 215 lignes.
- `ToolAgentOrchestrator.TestHooks.cs` : supprime.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check` :
  - uniquement des avertissements CRLF/LF deja connus, pas d'erreur de whitespace bloquante.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Les plus gros fichiers actifs restants sont maintenant :
  - `ToolAgentOrchestrator.RagMultiSearchExecution.cs` : 1362 lignes.
  - `ToolAgentOrchestrator.RunPipeline.cs` : 1064 lignes.
  - `ToolAgentOrchestrator.SourceBackedPlanningTitleQuality.cs` : 1037 lignes.
  - `ToolAgentOrchestrator.SourceBackedOptionTitleCleaning.cs` : 1018 lignes.
  - `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceability.cs` : 1008 lignes.
  - `ToolAgentOrchestrator.SourceBackedRetrievalQueries.cs` : 1002 lignes.
- Ne pas decouper `RunPipeline.cs` sans refonte volontaire de `RunAsync`.
- Inspecter les fichiers SourceBacked autour de 1000 lignes pour confirmer s'ils sont coherents ou s'ils cachent plusieurs roles.

## Mise a jour - 2026-07-10 - extraction de SourceBackedRetrievalQueries.cs

Contexte :

- `RagMultiSearchExecution.cs` a ete inspecte : il contient essentiellement une seule unite d'execution `ExecRagMultiSearchAsync`.
- `RunPipeline.cs` a ete inspecte : il contient essentiellement une seule unite d'orchestration `RunAsync`.
- Ces deux fichiers ne sont donc pas decoupes artificiellement pour l'instant.
- `ToolAgentOrchestrator.SourceBackedRetrievalQueries.cs` melangeait en revanche plusieurs familles de retrieval tres differentes :
  - discovery de candidats ;
  - sanitization des requetes generees par le LLM ;
  - limites/topK/rounds ;
  - navigation/broad discovery ;
  - expansions planning/action/technical/comparative ;
  - queries generiques semantiques ;
  - signaux document type/version.

Changement realise :

- `ToolAgentOrchestrator.SourceBackedRetrievalQueries.cs` a ete supprime comme fichier actif unique.
- Il a ete remplace par cinq fichiers specialises :
  - `ToolAgentOrchestrator.SourceBackedRetrievalDiscoveryQueries.cs` : candidate/anchor discovery et sanitization des queries LLM.
  - `ToolAgentOrchestrator.SourceBackedRetrievalNavigationQueries.cs` : topK/limites d'exploration, navigation discovery et broad source discovery.
  - `ToolAgentOrchestrator.SourceBackedRetrievalPlanningQueriesSupport.cs` : suffixes/termes d'expansion planning, limites action/technical/comparative, variantes de termes.
  - `ToolAgentOrchestrator.SourceBackedGenericRetrievalQueries.cs` : queries generiques semantiques multi-langues.
  - `ToolAgentOrchestrator.SourceBackedDocumentTypeRetrievalSignals.cs` : signaux document type/version, SDS/FDS et scoring d'ancrage document-type.
- Le test d'architecture `SourceBackedRagArchitectureTests` a ete mis a jour pour surveiller explicitement les nouveaux fichiers et leurs limites de taille.
- Aucun comportement volontairement change.
- Aucun hardcoding metier/categorie ajoute ; les signaux document-type restent generiques au retrieval documentaire.

Etat structurel courant :

- `ToolAgentOrchestrator.SourceBackedRetrievalDiscoveryQueries.cs` : 301 lignes.
- `ToolAgentOrchestrator.SourceBackedRetrievalNavigationQueries.cs` : 292 lignes.
- `ToolAgentOrchestrator.SourceBackedRetrievalPlanningQueriesSupport.cs` : 197 lignes.
- `ToolAgentOrchestrator.SourceBackedGenericRetrievalQueries.cs` : 172 lignes.
- `ToolAgentOrchestrator.SourceBackedDocumentTypeRetrievalSignals.cs` : 96 lignes.
- `ToolAgentOrchestrator.SourceBackedRetrievalQueries.cs` : supprime.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - premiere relance OK apres extraction ;
  - relance supplementaire OK apres mise a jour du test d'architecture.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - premiere relance : echec attendu car le garde-fou cherchait encore l'ancien fichier ;
  - test d'architecture mis a jour ;
  - relance finale OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check` :
  - uniquement des avertissements CRLF/LF deja connus, pas d'erreur de whitespace bloquante.
- Processus :
  - aucun `dotnet` restant apres validation.

Prochaine cible :

- Les plus gros fichiers actifs restants sont maintenant :
  - `ToolAgentOrchestrator.RagMultiSearchExecution.cs` : 1362 lignes.
  - `ToolAgentOrchestrator.RunPipeline.cs` : 1064 lignes.
  - `ToolAgentOrchestrator.SourceBackedPlanningTitleQuality.cs` : 1037 lignes.
  - `ToolAgentOrchestrator.SourceBackedOptionTitleCleaning.cs` : 1018 lignes.
  - `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceability.cs` : 1008 lignes.
- Les fichiers title-quality/title-cleaning semblent tres specialises, meme s'ils sont longs.
- Inspecter `SourceBackedDocumentVersionTraceability.cs` : il pourrait cacher plusieurs etapes d'une meme feature (answer/source derivation/selection/search/ranking).

## Mise a jour - 2026-07-10 - extraction de DocumentVersionTraceability par etapes

Contexte :

- `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceability.cs` etait juste au-dessus de 1000 lignes.
- La longueur seule n'aurait pas justifie une extraction.
- L'inspection a montre plusieurs etapes distinctes dans une meme feature :
  - construction de reponse et derivation des sources ;
  - selection/scoring des hits ;
  - detection d'intention et construction des requetes ;
  - status/ranking/versioning et cles de references documentaires.

Changement realise :

- `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceability.cs` a ete supprime comme fichier actif unique.
- Il a ete remplace par quatre fichiers specialises :
  - `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceabilityAnswer.cs` : reponse finale traceability et derivation des `SourceRef`.
  - `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceabilityHitSelection.cs` : selection des hits, scoring, overlap et signaux historical/contrast.
  - `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceabilityQueries.cs` : detection de requete traceability, construction des requetes exactes/broadened et termes surface/status.
  - `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceabilityRanking.cs` : status hits, extraction annees, ranking latest/historical/contrast et cles de references standard.
- Le test d'architecture `SourceBackedRagArchitectureTests` a ete mis a jour pour surveiller explicitement ces quatre fichiers.
- Aucun comportement volontairement change.
- Aucun hardcoding metier ou categorie ajoute.

Etat structurel courant :

- `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceabilityAnswer.cs` : 160 lignes.
- `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceabilityHitSelection.cs` : 372 lignes.
- `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceabilityQueries.cs` : 180 lignes.
- `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceabilityRanking.cs` : 331 lignes.
- `ToolAgentOrchestrator.SourceBackedDocumentVersionTraceability.cs` : supprime.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Recherche hardcoding metier dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence cuisine/meal/repas/diner/dinner.
- Recherche active legacy dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence surveillee.
- Recherche runtime active `BuildRagEvidenceFallbackAnswer(` dans `client\SAAIA.Client.WinUI\ToolAgent` hors `OLD` :
  - aucune occurrence.
- `git diff --check` :
  - uniquement des avertissements CRLF/LF deja connus, pas d'erreur de whitespace bloquante.
- Processus :
  - aucun `dotnet` restant apres validation.

Etat apres ce jalon :

- Les seuls fichiers actifs `ToolAgentOrchestrator*.cs` au-dessus de 1000 lignes sont :
  - `ToolAgentOrchestrator.RagMultiSearchExecution.cs` : 1362 lignes, une seule methode d'execution principale.
  - `ToolAgentOrchestrator.RunPipeline.cs` : 1064 lignes, une seule methode d'orchestration principale.
  - `ToolAgentOrchestrator.SourceBackedPlanningTitleQuality.cs` : 1037 lignes, helpers specialises de qualite titre.
  - `ToolAgentOrchestrator.SourceBackedOptionTitleCleaning.cs` : 1018 lignes, helpers specialises de nettoyage titre.
- Conformement a la consigne utilisateur, ces fichiers ne doivent pas etre decoupes juste pour passer sous un seuil arbitraire.
- Prochaine etape recommandee : basculer de la restructuration de fichiers vers la validation fonctionnelle plus large, puis preparer le vrai test UI.

## Mise a jour - 2026-07-10 - tentative de validation ToolAgent large

Commande lancee :

- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --verbosity:minimal /m:1 /nr:false`

Resultat :

- La commande a depasse le timeout de 5 minutes.
- Deux processus `dotnet` restants apres timeout ont ete arretes manuellement :
  - `19132`
  - `28408`
- Un fichier de synthese des echecs a ete genere :
  - `artifacts\codex-test-toolagent-full-failures.txt`

Constat :

- La suite complete n'est pas utilisable comme validation verte a ce stade.
- Elle a produit 90 lignes d'echecs avant timeout.
- Les echecs visibles se concentrent surtout dans :
  - `RagContextBudgetRegressionTests`
  - `StructuredPlanningCoverageTests`
  - `ApiClientDocumentsTransitionTests`
  - `UiLocalizationSafetyNetTests`
- Beaucoup d'echecs portent sur des comportements fonctionnels deja au coeur du chantier :
  - planning hebdomadaire/axes explicites ;
  - fallback readable/safe ;
  - localisation de phrases de fallback ;
  - source leads/writer coverage ;
  - generic collection ;
  - exact technical/source-backed requests ;
  - navigation/documentary probes.

Interpretation prudente :

- Les decoupages mecaniques realises dans ce jalon restent valides par :
  - build OK ;
  - architecture OK 24/24 ;
  - `SourceBackedRag` OK 42/42 ;
  - scans hardcoding/legacy/fallback OK.
- La suite complete revele cependant que le projet conserve encore une dette de validation fonctionnelle large.
- Il ne faut pas patcher ces tests au hasard.
- Prochaine etape professionnelle : classifier ces 90 echecs entre :
  - tests obsoletes qui verifient des comportements de l'ancienne architecture ;
  - tests encore valides qui signalent un vrai gap du nouveau pipeline ;
  - tests de localisation/encodage qui doivent etre corriges proprement ;
  - tests trop lents/flaky a isoler.

Decision :

- Ne pas poursuivre le decoupage de fichiers par taille.
- Basculer le travail vers la stabilisation de validation :
  - definir le set de tests canonique du nouveau pipeline ;
  - corriger ou requalifier les tests larges ;
  - seulement ensuite preparer le test UI reel.

## Mise a jour - 2026-07-10 - triage initial des echecs larges et correction UI source labels

Triage initial de `artifacts\codex-test-toolagent-full-failures.txt` :

- 90 lignes d'echecs recensees avant timeout de la suite complete.
- Repartition par classe de tests :
  - `RagContextBudgetRegressionTests` : 86 echecs.
  - `StructuredPlanningCoverageTests` : 2 echecs.
  - `ApiClientDocumentsTransitionTests` : 1 echec.
  - `UiLocalizationSafetyNetTests` : 1 echec.
- Repartition thematique approximative :
  - planning : 44.
  - fallback : 26.
  - localisation/langue : 25.
  - writer : 15.
  - evidence/source-backed answer : 12.
  - navigation/documentary : 11.
  - exact/technical : 6.
  - generic collection : 5.
  - pairing : 3.
  - quantity : 1.

Decision de priorisation :

- Ne pas attaquer les 86 echecs `RagContextBudgetRegressionTests` en masse.
- Traiter d'abord l'echec UI/source-card, car il est :
  - hors du gros bloc historique planning ;
  - directement lie au critere final UI + source cards ;
  - aligné avec l'objectif de remplacer les labels visibles codés en dur par des champs structures et helpers localises.

Probleme corrige :

- `UiLocalizationSafetyNetTests.Visible_source_page_labels_use_localized_prefix_helpers` detectait encore des labels visibles `p.` construits a la main.
- Source principale :
  - `ToolAgent\SourceBackedRag\SourceBackedUiPayloadMapper.cs`
    - le champ `Label` contenait `"{doc} p. {page}"`.
    - correction : `Label` contient seulement le nom/source visible ; `PageStart`/`PageEnd` restent les champs structures utilises par `SourcesCardsControl.GetPagesLabel`.
- Source secondaire :
  - `ToolAgentOrchestrator.SourceBackedWriterEvidenceCitations.cs`
    - `BuildInlineSourceCitationForWriter` construisait encore `"(source: {doc} p.{page})"`.
    - correction : le helper prend maintenant `language` et utilise `SourceBackedPagePrefix(language)`.
  - Appels mis a jour dans :
    - `ToolAgentOrchestrator.SourceBackedPlanningAnswerRepair.cs`
    - `ToolAgentOrchestrator.SourceBackedWriterEvidenceInventory.cs`
    - `ToolAgentOrchestrator.SourceBackedWriterEvidenceCitations.cs`
- Test pipeline mis a jour :
  - `SourceBackedRagPipelineTests` verifie maintenant que `Label == "checklist.pdf"` et que les pages restent portees par `PageStart`/`PageEnd`.

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK, build succeeded.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~UiLocalizationSafetyNetTests.Visible_source_page_labels_use_localized_prefix_helpers" --verbosity:minimal /m:1 /nr:false`
  - OK, 1/1.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~UiLocalizationSafetyNetTests" --verbosity:minimal /m:1 /nr:false`
  - OK, classe UI localisation verte.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagPipelineTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 10/10.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.
- Scans :
  - hardcoding metier hors `OLD` : OK.
  - legacy actif hors `OLD` : OK.
  - fallback brut runtime hors `OLD` : OK.
  - labels visibles `p.` codés en dur dans `ToolAgent` + `RagChatAgent` : OK.
- Processus :
  - aucun `dotnet` restant apres validation.

Impact :

- Progression concrete vers le critere final UI :
  - les sources UI portent les pages en champs structures ;
  - le rendu visible passe par les helpers localises ;
  - le mapper canonique ne reinsere plus de page label non localise dans `Label`.

Suite recommandee :

- Continuer la stabilisation par petits ensembles valides.
- Prochain candidat sain :
  - traiter l'echec `ApiClientDocumentsTransitionTests.ToolAgent_rag_multi_search_probes_catalog_categories_before_unscoped_source_exploration`, car il touche l'ordre d'exploration RAG categorie/source et peut indiquer une regression d'orchestration.
- Ensuite seulement revenir aux familles massives `RagContextBudgetRegressionTests`, en separant tests obsoletes de vrais gaps.

## Mise a jour - 2026-07-10 - correction du category probe source exploration

Probleme corrige :

- `ApiClientDocumentsTransitionTests.ToolAgent_rag_multi_search_probes_catalog_categories_before_unscoped_source_exploration` echouait encore.
- Le mode `rag_multi_search` en `researchMode = "source_exploration"` pouvait partir sur une recherche non scopee (`categoryPath = null`) alors que la memoire contenait deja un snapshot de categories.
- Le probleme n'etait pas metier : il venait du fait que le probe de categories refusait de construire des candidats quand il n'avait pas de signal lexical/catalogue assez fort.
- Dans ce cas, le code tombait ensuite sur un appel RAG global non scope, ce qui va contre l'objectif d'orchestration propre : explorer les scopes connus avant d'elargir.

Correction appliquee :

- Fichier modifie :
  - `client\SAAIA.Client.WinUI\ToolAgent\Parts\ToolAgentOrchestrator.RagMultiSearchExecution.cs`
- `TryInferCategoryScopeFromCatalogProbeAsync` construit maintenant des candidats de probe a partir des categories connues quand aucun candidat fort n'existe.
- Ce fallback reste generique :
  - aucune categorie metier n'est codee en dur ;
  - les candidats viennent du snapshot catalogue/memoire ;
  - le tri utilise seulement des signaux mecaniques generaux (`TotalDocuments`, `Ordinal`) ;
  - l'acceptation du meilleur scope exige toujours un resultat RAG reel (`HitCount > 0`) et le nombre requis de requetes matchees.
- Une trace a ete ajoutee :
  - `rag.multi_search.category_probe.fallback_candidates`
  - raison : `source_exploration_without_strong_catalog_hint`

Validation executee :

- `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ApiClientDocumentsTransitionTests.ToolAgent_rag_multi_search_probes_catalog_categories_before_unscoped_source_exploration" --verbosity:minimal /m:1 /nr:false`
  - OK, 1/1.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~ApiClientDocumentsTransitionTests.ToolAgent_rag_multi_search" --verbosity:minimal /m:1 /nr:false`
  - OK, 22/22.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRagArchitectureTests" --verbosity:minimal /m:1 /nr:false`
  - OK, 24/24.
- `dotnet test client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-build --filter "FullyQualifiedName~SourceBackedRag" --verbosity:minimal /m:1 /nr:false`
  - OK, 42/42.

Scans de securite apres correction :

- Hardcoding metier actif hors `OLD` :
  - OK, aucun match.
- Legacy actif hors `OLD` :
  - OK, aucun match.
- Appel runtime brut a `BuildRagEvidenceFallbackAnswer` hors `OLD` :
  - OK, aucun appel actif.
- Labels visibles de pages `p.` codes en dur dans `ToolAgent` + `RagChatAgent` :
  - OK, aucun match.
- Processus `dotnet` restant :
  - OK, aucun processus.

Impact :

- Le pipeline source exploration utilise mieux la memoire/catalogue au bon moment.
- Le LLM reste responsable de la decision semantique finale.
- Le code ne decide pas qu'une categorie "repond" a la question ; il teste mecaniquement les scopes connus, garde celui qui produit des preuves, et laisse ensuite le pipeline evidence/writer/verifier faire son travail.
- Le correctif est compatible avec toutes les categories de documents.

Suite recommandee :

- Passer aux deux echecs `StructuredPlanningCoverageTests`, car ils sont isoles et plus proches du coeur planner/evidence que les 86 echecs historiques `RagContextBudgetRegressionTests`.
- Continuer a eviter les patchs par domaine et ne corriger que des contrats, traces, budgets, outils ou orchestration generique.

## Mise a jour - 2026-07-10 - correction de deux gaps StructuredPlanningCoverageTests

Problemes corriges :

- `StructuredPlanningCoverageTests.Structured_meal_planning_navigation_followup_skips_mechanical_noise_and_keeps_candidate_anchors`
  - Le follow-up de navigation conservait un fragment OCR fusionne du type `INGREDIENTSPREPARATION1`.
  - Correction generique dans `ToolAgentOrchestrator.SourceBackedPlanningTitleQuality.cs` :
    - rejet des fragments longs, sans espace, majoritairement en majuscules et termines par un numero ;
    - rejet des sequences structurelles fusionnees du type `ingredients/components/materials/requirements` + `preparation/procedure/instructions/method/steps`.
  - Ce n'est pas une regle cuisine : le filtre vise des formes de bruit OCR et de labels structurels fusionnes.

- `StructuredPlanningCoverageTests.Weekly_meal_plan_uses_retrieval_query_routes_to_fill_observed_recipe_slots`
  - La trace `slot_fit` disait `assigned_slots=13|required_slots=20`, mais `EvaluateSourceBackedPlanningCoverage` pouvait encore conclure `adequate=true` parce que le nombre de candidats distincts etait suffisant.
  - Correction generique dans `ToolAgentOrchestrator.SourceBackedEvidenceSufficiency.cs` :
    - l'evaluation de couverture reconstruit maintenant le `slot_fit` quand la demande contient une grille explicite ;
    - une couverture avec `routeEvidence=true` mais trop peu de slots assignes n'est plus adequate ;
    - si la demande exige explicitement une preuve par slot, l'absence de route evidence rend aussi la couverture incomplete.
  - Trace ajoutee :
    - `trace_path=rag.planning.coverage|trace_step=slot_fit|stage=slot_fit`

Validation executee :

- Build :
  - `dotnet build client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj --no-restore --verbosity:minimal /m:1 /nr:false /p:UseSharedCompilation=false /p:RunAnalyzers=false`
  - OK.
- Tests cibles :
  - `StructuredPlanningCoverageTests.Structured_meal_planning_navigation_followup_skips_mechanical_noise_and_keeps_candidate_anchors`
  - OK, 1/1.
  - `StructuredPlanningCoverageTests.Weekly_meal_plan_uses_retrieval_query_routes_to_fill_observed_recipe_slots`
  - OK, 1/1.
- Test combine des deux corrections :
  - OK, 2/2.
- `SourceBackedRagArchitectureTests`
  - OK, 24/24.
- `SourceBackedRag`
  - OK, 42/42.

Scans de securite apres correction :

- Hardcoding metier actif hors `OLD` :
  - OK, aucun match.
- Legacy actif hors `OLD` :
  - OK, aucun match.
- Appel runtime brut a `BuildRagEvidenceFallbackAnswer` hors `OLD` :
  - OK, aucun appel actif.
- Labels visibles de pages `p.` codes en dur dans `ToolAgent` + `RagChatAgent` :
  - OK, aucun match.
- Processus `dotnet` restant :
  - OK, aucun processus.

Point important decouvert :

- La classe complete `StructuredPlanningCoverageTests` ne doit pas encore etre consideree comme gate verte.
- Un run de classe entiere a depasse 15 minutes et a revele plusieurs echecs supplementaires avant timeout.
- Les echecs vus touchent notamment :
  - extraction de titres cites ;
  - suppression trop forte ou trop faible de candidats page-local/content-card ;
  - bruit d'inventaire UI ;
  - contexte document comme evidence candidate ;
  - remplissage de slots par candidats mal routes.
- Conclusion :
  - les deux echecs isoles du rapport initial sont corriges ;
  - la classe globale contient encore de la dette fonctionnelle qui doit etre traitee par sous-ensembles cibles, pas par patchs en vrac.

Suite recommandee :

- Extraire les echecs `StructuredPlanningCoverageTests` restants en petits groupes :
  - candidate extraction/title ownership ;
  - visible source citation acceptance ;
  - OCR/noise filtering ;
  - slot routing/grid fit ;
  - writer inventory noise.
- Corriger chaque groupe via des regles generiques et traces, sans introduire de logique de categorie.
