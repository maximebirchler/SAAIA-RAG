# Architecture des fournisseurs LLM — Local, OpenAI Terra et RunPod

Date de référence : 12 septembre 2026
Statut produit : **TESTE_NON_APPROUVE**

Ce document décrit la restructuration du runtime LLM de SAAIA. Elle permet
d'utiliser temporairement GPT-5.6 Terra comme baseline de développement, puis
un llama-server distant sur RunPod pour les benchmarks. Le petit modèle local
reste la capacité par défaut ; la direction produit amendée prévoit ensuite un
grand modèle sur un serveur on-prem du client pour l'option avancée.

La frontière du petit modèle local est déjà qualifiée sur la banque A755 connue :
14 cas répétés trois fois, soit 42/42 résultats acceptés. Cette preuve autorise
l'ouverture du chantier avancé ; elle n'approuve ni le produit complet, ni le
parcours WinUI avancé, ni le planning de repas 5 × 4.

## A. Analyse initiale

### Chemin applicatif avant la restructuration

```text
Utilisateur WinUI
  → RagChatAgent
  → ToolAgentOrchestrator / Router
  → Tools et ApiClient
  → backend SAAIA / retrieval / EvidenceBundle
  → Writer puis Critic éventuel
  → OpenAiLlmClient
  → llama.cpp local
  → flux de texte vers WinUI
```

Le code métier disposait déjà de `ILlmClient` et de contrats spécialisés pour
les tools, les structured outputs, le comptage et le contexte. Le couplage
principal se trouvait dans `RagChatAgent`, qui construisait un adapter privé
autour de `OpenAiLlmClient`. `MainWindow` configurait directement l'URL et le
modèle local, lançait le warmup llama.cpp et pouvait remplacer silencieusement
le modèle configuré par le premier résultat de `/models`.

Les points existants réutilisés sont :

- lancement et arrêt de llama.cpp : `Services/LlamaCppProcessManager.cs` ;
- configuration locale : `Services/AppSettings.cs` ;
- catalogue et profils qualifiés : `ModelCatalogStore`,
  `GovernanceArtifactStore` et `WarmupProfileStore` ;
- prompts Router, Writer et Critic : `ToolAgent/PromptCatalog.cs` et les
  composants `ToolAgentOrchestrator.*` ;
- structured outputs et tool calls : `OpenAiLlmClient` et les contrats
  `SourceBackedRag` ;
- streaming : `OpenAiLlmClient.ChatStreamAsync`, relayé sans changement de
  ViewModel par l'orchestrateur et `RagChatAgent` ;
- annulation : `CancellationToken` déjà propagé sur toute la chaîne ;
- retries historiques : uniquement les replis de format JSON propres à
  llama.cpp. Aucun changement de fournisseur automatique n'existe.

### Couplages retirés

`RagChatAgent`, le Router, le Writer et le Critic utilisent désormais la même
instance `ILlmProvider`. Ils ne choisissent ni URL, ni modèle, ni fournisseur.
Le bootstrap local, la détection de modèle et le warmup ne s'exécutent que si le
mode actif est `Local`. La logique RAG, l'ingestion, PostgreSQL, Qdrant, TEI,
les embeddings, le reranking et les contrats d'EvidenceBundle ne sont pas
modifiés par ce lot.

## B. Architecture retenue

Deux niveaux de fournisseur partagent le protocole OpenAI-compatible mais ont
des responsabilités différentes. `ILlmProvider` est le runtime direct du
client ; il permet de qualifier le modèle local et d'effectuer des sondes DEV
ou BENCH. Le parcours produit validé conserve ce runtime en `Local` et utilise
`IAdvancedAnalysisProvider` dans le backend quand la frontière A755 demande une
capacité avancée.

```text
Parcours produit
  Client : petit modèle Local -> frontière A755
  Serveur : job AdvancedAnalysis -> fournisseur du grand modèle
            openai-dev -> runpod-bench -> customer-server

Sondes isolées
  Client : OpenAiDev ou RunPodBench directement
```

Les sondes directes vérifient les dialectes HTTP, les structured outputs, les
tools et le streaming. Elles ne prouvent pas le routage local -> serveur, la
durabilité du job, la revalidation des preuves ou le retour des sources. Ces
preuves sont acquises avec le harnais `test-advanced-analysis-agent-bank.ps1`.

La conception détaillée du fournisseur serveur se trouve dans
`advanced-analysis-server-provider-v1.md`. La séparation future entre droits de
licence, topologie d'installation et profil technique est définie dans
`llm-license-installation-vision.md` sans étendre le périmètre de ce lot.

```text
RagChatAgent / ToolAgentOrchestrator
                 │
                 ▼
            ILlmProvider
        ┌────────┼───────────┐
        ▼        ▼           ▼
      Local   OpenAiDev   RunPodBench
   llama.cpp   Terra      llama-server
      PC       API DEV    GPU de bench
```

`ILlmProvider` étend les contrats LLM existants afin de ne pas créer une seconde
architecture. Il expose l'identité du runtime, le streaming, les structured
outputs, les tools, la configuration de génération, un scope de tour et des
métriques normalisées.

Les responsabilités sont réparties ainsi :

- `LlmProviderConfiguration` charge la configuration commune et applique les
  variables d'environnement ;
- `LlmProviderFactory` valide la policy, lit le secret demandé et crée exactement
  un fournisseur par exécution ;
- `LocalLlmProvider`, `OpenAiDevLlmProvider` et `RunPodBenchLlmProvider`
  adaptent le transport OpenAI-compatible existant ;
- `OpenAiLlmClient` gère le protocole HTTP, l'authentification, les différences
  `max_tokens` / `max_completion_tokens`, les tools, le JSON Schema et le SSE ;
- `LlmCostBudgetGuard` réserve un coût maximal avant chaque appel Terra,
  enregistre l'usage retourné et bloque localement les dépassements ;
- `ToolAgentOrchestrator.RuntimeSnapshot` identifie le fournisseur, le modèle,
  le profil et l'état du budget dans les artefacts de benchmark.

Le contexte déclaré par le provider alimente aussi les budgets Router,
source-backed et Writer. Terra déclare sa fenêtre de 1 050 000 tokens ; RunPod
utilise le `contextSize` du profil de benchmark. Les plafonds internes de SAAIA
continuent de borner la quantité réelle de données placée dans un prompt.

Pour un tour, Router, Writer et Critic utilisent obligatoirement la même
instance. Une indisponibilité Terra ou RunPod échoue explicitement ; elle ne
provoque aucun basculement vers un autre fournisseur.

### Données envoyées à un fournisseur externe

Selon le rôle, le fournisseur reçoit seulement les instructions nécessaires,
le contexte conversationnel retenu, le manifeste des tools, leurs résultats
structurés et l'EvidenceBundle construit pour la réponse. Le corpus complet,
PostgreSQL, Qdrant, les journaux, les secrets et les informations
d'administration ne sont pas envoyés par le provider.

Cette règle protège la portée technique. Elle ne remplace pas une décision de
confidentialité : un test sur un corpus privé transmet à OpenAI ou RunPod les
extraits inclus dans le prompt. Les campagnes doivent donc consigner le corpus
et l'autorisation utilisés.

## C. Fichiers du lot

| Chemin | Action | But |
|---|---|---|
| `config/llm-providers.dev.json` | créé | défaut local, Terra, RunPod, prix, budget et délais |
| `ToolAgent/LlmProviderContracts.cs` | créé | contrat unique, identité, usage, métriques et profil runtime |
| `ToolAgent/LlmProviderConfiguration.cs` | créé | chargement, policies, validation et factory |
| `ToolAgent/OpenAiCompatibleLlmProvider.cs` | créé | implémentations Local, Terra et RunPod |
| `ToolAgent/LlmCostBudgetGuard.cs` | créé | journal de coût persistant et coupe-circuits locaux |
| `Services/OpenAiLlmClient.cs` | modifié | dialectes, bearer, Terra, usage et streaming SSE |
| `Services/RagChatAgent.cs` | modifié | injection de `ILlmProvider` commun |
| `SourceBackedAgentContracts.cs` | modifié | usage cache/reasoning et nombre de retries |
| `ToolAgentOrchestrator.RuntimeSnapshot.cs` | modifié | identité fournisseur et budget dans les diagnostics |
| `MainWindow/State.cs` | modifié | stockage de l'unique provider actif |
| `MainWindow/SetupLifecycle.cs` | modifié | sélection explicite et bootstrap local conditionnel |
| `MainWindow/Core.cs` | modifié | démarrage llama.cpp limité au mode local |
| `Services/SupportBundleBuilder.cs` | modifié | expurgation défensive des secrets LLM externes |
| `SAAIA.Client.WinUI.csproj` | modifié | copie de la configuration dans le build |
| `LlmProviderArchitectureTests.cs` | créé | sélection, sécurité, budget, erreurs et streaming simulé |
| `LiveOpenAiTerraProviderTests.cs` | créé | sonde Terra réelle, opt-in et payante |
| `LiveRunPodProviderTests.cs` | créé | sonde RunPod réelle, opt-in et payante |
| `SupportBundleMemoryDiagnosticsTests.cs` | modifié | preuve de redaction du ZIP de support |
| `config/advanced-capacity-validation.v1.json` | créé | banque avancée gelée, dont le planning 5 × 4 |
| `tools/import-llm-secret-from-clipboard.ps1` | créé | import d'une clé dans le coffre DPAPI sans argument secret |
| `tools/test-openai-terra-provider.ps1` | créé | lancement explicite de la sonde Terra |
| `tools/test-openai-terra-agent-bank.ps1` | créé | campagne Terra end-to-end et registre de coût global |
| `tools/test-runpod-llm-provider.ps1` | créé | lancement explicite de la sonde RunPod |
| `tools/start-client-openai-terra-dev.ps1` | créé | lancement WinUI en mode Terra DEV |
| `tools/start-client-runpod-bench.ps1` | créé | lancement WinUI en mode RunPod BENCH |

## D. Configuration

La source versionnée est `config/llm-providers.dev.json`. Elle sélectionne
`Local` avec la policy `ProductionLocal`. Les modes externes exigent à la fois
un mode et une policy compatibles.

### Local

```powershell
$env:SAAIA_LLM_PROVIDER_MODE = "Local"
$env:SAAIA_LLM_EXTERNAL_POLICY = "ProductionLocal"
powershell -NoProfile -ExecutionPolicy Bypass -File .\client\scripts\run-client-x64.ps1
```

Le modèle, le port, le runtime et le profil qualifié restent fournis par
`AppSettings` et la gouvernance locale existante.

### OpenAI Terra DEV

La variable d'environnement reste possible. Le chemin recommandé sur Windows
utilise le coffre SAAIA existant : copier la clé dans le presse-papiers, puis
l'importer sous DPAPI pour l'utilisateur Windows courant. Le script vide le
presse-papiers après l'import.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\import-llm-secret-from-clipboard.ps1 -Provider OpenAI
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\start-client-openai-terra-dev.ps1
```

Variables disponibles :

| Variable | Valeur par défaut | Rôle |
|---|---:|---|
| `SAAIA_LLM_PROVIDER_MODE` | `Local` | mettre `OpenAiDev` |
| `SAAIA_LLM_EXTERNAL_POLICY` | `ProductionLocal` | mettre `DevelopmentExternalAllowed` |
| `SAAIA_OPENAI_API_KEY` | aucune | priorité sur le secret DPAPI ; jamais versionnée |
| `SAAIA_OPENAI_BASE_URL` | `https://api.openai.com/v1` | endpoint |
| `SAAIA_OPENAI_MODEL` | `gpt-5.6-terra` | modèle ; override explicitement expérimental |
| `SAAIA_OPENAI_REASONING_EFFORT` | `low` | effort de raisonnement |
| `SAAIA_OPENAI_REQUEST_TIMEOUT_SECONDS` | `120` | délai par appel |
| `SAAIA_OPENAI_BUDGET_USD` | `25` | autorisation déclarée |
| `SAAIA_OPENAI_SOFT_LIMIT_USD` | `20` | alerte locale |
| `SAAIA_OPENAI_HARD_LIMIT_USD` | `24` | arrêt local avant 25 $ |
| `SAAIA_OPENAI_MAX_COST_PER_TURN_USD` | `0.50` | plafond réservé par tour |
| `SAAIA_OPENAI_MAX_CALLS_PER_TURN` | `32` | plafond d'appels par tour |
| `SAAIA_OPENAI_USAGE_LEDGER_PATH` | `%LOCALAPPDATA%\SAAIA\llm-dev\openai-terra-usage.jsonl` | journal sans prompt ni secret |

Aux tarifs publiés de Terra le 11 septembre 2026, un appel de 7 000 tokens
d'entrée et 1 000 tokens de sortie coûte environ 0,026 $ hors cache. Le calcul
théorique de 25 $ correspond donc à environ 961 appels de cette taille. Un tour
SAAIA peut comporter plusieurs appels Router/Writer/Critic ; il ne faut pas
confondre ce nombre d'appels avec 961 questions end-to-end.

La configuration locale réserve 1 $ de marge : elle refuse les nouveaux appels
à 24 $, avec alerte à 20 $. Le plafond OpenAI du projet reste une seconde barrière
indépendante à configurer sur la plateforme.

### RunPod BENCH

```powershell
$env:SAAIA_RUNPOD_API_KEY = "<secret utilisateur>"
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\start-client-runpod-bench.ps1 `
  -BaseUrl "https://<endpoint>/v1" `
  -ModelId "<model-id>" `
  -RuntimeProfile "qwen-32b-q5-profile-1"
```

Variables disponibles :

- sélection : `SAAIA_LLM_PROVIDER_MODE=RunPodBench` et
  `SAAIA_LLM_EXTERNAL_POLICY=BenchmarkExternalAllowed` ;
- connexion : `SAAIA_RUNPOD_API_KEY`, `SAAIA_RUNPOD_BASE_URL`,
  `SAAIA_RUNPOD_MODEL`, `SAAIA_RUNPOD_REQUEST_TIMEOUT_SECONDS` ;
- identité : `SAAIA_RUNPOD_RUNTIME_PROFILE`, `SAAIA_RUNPOD_MODEL_PATH`,
  `SAAIA_RUNPOD_QUANTIZATION` ;
- runtime : `SAAIA_RUNPOD_CTX_SIZE`, `SAAIA_RUNPOD_BATCH_SIZE`,
  `SAAIA_RUNPOD_UBATCH_SIZE`, `SAAIA_RUNPOD_THREADS`,
  `SAAIA_RUNPOD_THREADS_BATCH`, `SAAIA_RUNPOD_GPU_LAYERS`,
  `SAAIA_RUNPOD_FLASH_ATTN`.

Ces paramètres décrivent le serveur de benchmark et apparaissent dans les
artefacts. Le démarrage effectif de llama-server sur le pod reste géré par le
script ou le template RunPod choisi, hors de la logique métier SAAIA.

Fermer le client termine le run SAAIA. Arrêter ensuite le pod depuis RunPod pour
arrêter sa facturation. Une nouvelle console sans ces variables, ou le mode
`Local` explicite, désactive le benchmark côté client.

## E. Tests automatisés

```powershell
dotnet restore .\RAG.sln -p:Platform=x64
dotnet build .\RAG.sln -c Debug -p:Platform=x64 --no-restore
dotnet test .\RAG.sln -c Debug -p:Platform=x64 --no-build
```

Le lot ciblé peut être contrôlé rapidement avec :

```powershell
dotnet test .\client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj `
  -c Debug -p:Platform=x64 `
  --filter "FullyQualifiedName~LlmProviderArchitectureTests|FullyQualifiedName~OpenAiLlmClientTests|FullyQualifiedName~OpenAiCompatLlmClientTests|FullyQualifiedName~SupportBundleMemoryDiagnosticsTests"
```

Les tests live quittent sans appel si leur gate n'est pas explicitement activée.
Ils ne doivent pas être ajoutés à une CI payante.

## F. Validation manuelle OpenAI

1. Créer un projet API de développement distinct et lui appliquer un plafond
   inférieur ou égal au budget autorisé.
2. Importer la clé depuis le presse-papiers dans le coffre DPAPI avec
   `import-llm-secret-from-clipboard.ps1 -Provider OpenAI`, ou la placer
   temporairement dans `SAAIA_OPENAI_API_KEY`.
3. Lancer la sonde minimale de trois appels :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test-openai-terra-provider.ps1
   ```

4. Vérifier l'artefact : provider `openai`, modèle `gpt-5.6-terra`, Router JSON
   valide, tool call natif, réponse Writer streamée, trois métriques réussies,
   usage et coût.
5. Lancer le cas avancé gelé, d'abord sur une seule répétition :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test-openai-terra-agent-bank.ps1 `
     -Ids A755-ADV-01-meal-grid-5x4 -Repetitions 1
   ```

   Le journal global `%LOCALAPPDATA%\SAAIA\llm-dev\openai-terra-usage.jsonl`
   est relu avant chaque processus. La configuration alerte à 20 $, refuse les
   nouveaux appels à 24 $, limite chaque tour à 0,50 $ et à 32 appels.
6. Après examen et corrections générales, rejouer le cas trois fois. Un exit
   code vert signifie seulement que le harnais s'est terminé ; chaque réponse
   doit encore recevoir un verdict sémantique source par source.
7. Lancer WinUI avec `start-client-openai-terra-dev.ps1`, poser d'abord une
   question documentaire simple dont les preuves sont connues, puis un cas
   avancé. Vérifier dans le snapshot :

   ```text
   User → Terra Router → Tools/RAG SAAIA → EvidenceBundle
        → Terra Writer/Critic → streaming UI → cartes source
   ```

8. Inspecter chaque carte : bon fichier, révision, page et passage. Comparer la
   réponse aux preuves ; un test vert ne vaut pas approbation sémantique.

## G. Validation manuelle RunPod

1. Démarrer un pod GPU avec llama.cpp/llama-server et un modèle GGUF choisi.
2. Consigner modèle, hash, quantification, GPU et paramètres runtime.
3. Définir `SAAIA_RUNPOD_API_KEY`, puis exécuter :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test-runpod-llm-provider.ps1 `
     -BaseUrl "https://<endpoint>/v1" -ModelId "<model-id>" `
     -RuntimeProfile "<profile-id>"
   ```

4. Exiger `/models`, Router structuré et Writer streamé avant une campagne.
5. Lancer WinUI avec `start-client-runpod-bench.ps1` et rejouer exactement le
   même corpus, les mêmes questions, prompts, tools, EvidenceBundles et critères
   que Terra.
6. Arrêter le pod dès la fin du run et conserver son coût réel avec l'artefact.

## H. Validé, restant et dette

Validé mécaniquement dans ce lot : sélection déterministe des trois providers,
policy externe, absence de clé, configuration RunPod incomplète, dialectes HTTP,
authentification, JSON Schema, normalisation SSE, usage, coût, délai, annulation,
401/429/500, réseau indisponible, flux interrompu, coupe-circuit budgétaire et
redaction du support bundle. Le provider Local a également été exécuté contre
le runtime Qwen gelé : structured output, tool call natif et streaming sont
valides. La banque locale A755 connue a ensuite été rejouée trois fois sur le
SHA `5516cc1a` ; ses 42 réponses ont été acceptées après revue sémantique, sans
coût externe et avec le port 1234 libéré en fin de campagne. Cette preuve reste
une non-régression sur une banque déjà vue et ne remplace pas le holdout aveugle.

Le parcours produit local -> job serveur -> Terra a été exécuté réellement. Il
a démontré le handoff, Planner, les tools RAG, Writer, les métriques et les
résultats durables. Deux cas réussis ont été acceptés unitairement ; le planning
5 × 4 a révélé un défaut général d'ancrage entre cellule et preuve, corrigé dans
`b20fcc2`. Une campagne complète 3/3 sur ce descendant reste empêchée par le
plafond OpenAI Free de 50 requêtes par jour. Le compte financé n'a toujours pas
été promu automatiquement au Tier 1 malgré l'achat payé ; le support est saisi.

Le commit `4744d81` ferme aussi un écart de résilience du fournisseur avancé :
un timeout ou une rupture réseau pendant la lecture du corps HTTP est maintenant
normalisé en `advanced_llm_timeout` ou `advanced_llm_transport_error`. Une
annulation demandée par l'appelant reste une annulation. Les 33 tests ciblés et
les 2 150 tests backend Release passent ; une sonde live opt-in est ignorée.

Restent obligatoires avant approbation :

- la banque Terra complète 3/3 sur `b20fcc2` ou un descendant documentaire,
  après activation réelle du Tier 1 ;
- la validation qualitative et source par source de chaque répétition ;
- trois réussites consécutives du planning 5 × 4 sur état figé ;
- un test RunPod réel et la comparaison d'un modèle open-source ;
- la validation terminale WinUI d'une réponse avancée réussie et de ses cartes
  source ;
- un nouveau holdout aveugle après gel du code ;
- l'essai du modèle final sur un serveur client réellement dimensionné ;
- la décision de catalogue et les profils de warmup finaux.

Les prix Terra sont des métadonnées modifiables. La source officielle consultée
le 11 septembre 2026 est
<https://developers.openai.com/api/docs/models/gpt-5.6-terra>. Elle confirme
Chat Completions, streaming, function calling et structured outputs. Une
campagne ultérieure doit revérifier les prix et capacités avant dépense.

Rappel de destination :

```text
OpenAI Terra = baseline DEV temporaire
RunPod = infrastructure BENCH temporaire
petit llama.cpp local = capacité locale de production
grand modèle sur serveur on-prem client = capacité avancée finale
```
