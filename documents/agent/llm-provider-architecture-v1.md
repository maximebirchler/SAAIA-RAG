# Architecture des fournisseurs LLM — Local, OpenAI Terra et RunPod

Date de référence : 13 septembre 2026 ; les résultats antérieurs restent datés dans le texte.
Statut produit : **TESTE_NON_APPROUVE**

Ce document décrit la restructuration du runtime LLM de SAAIA. Elle permet
d'utiliser temporairement GPT-5.6 Terra comme baseline de développement, puis
un llama-server distant sur RunPod pour les benchmarks. Le petit modèle local
reste la capacité par défaut ; la direction produit amendée prévoit ensuite un
grand modèle sur un serveur on-prem du client pour l'option avancée.

Les routes du petit modèle local sont qualifiées sur la banque A755 connue :
14 cas répétés trois fois, soit 42/42 comportements acceptés, comprenant aussi
les transferts et clarifications. La dernière campagne connue A801 sur `8437c345`
compte neuf réponses locales terminées sur ces 42 résultats. Cette preuve autorise
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
Le dernier constructeur public de compatibilité qui acceptait directement un
`OpenAiLlmClient` et lui attribuait implicitement l'identité Local/llama.cpp a
été supprimé. Le constructeur public unique exige maintenant `ILlmProvider` ;
les probes live passent elles aussi par `LlmProviderFactory` avec leur endpoint,
leur modèle et leur profil explicitement fournis.
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
| `LlmProviderArchitectureTests.cs` | créé | sélection, sécurité, budget, erreurs, streaming simulé et constructeur provider obligatoire |
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

Le parcours produit utilise la clé protégée dans le coffre SAAIA. Après
autorisation explicite, copier la clé créée dans RunPod puis l'importer ainsi :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\tools\import-llm-secret-from-clipboard.ps1 `
  -Provider RunPod
```

Le script chiffre la valeur avec DPAPI pour l'utilisateur Windows courant et
efface le presse-papiers par défaut. La variable d'environnement ci-dessous
reste réservée aux probes directes temporaires :

```powershell
$env:SAAIA_RUNPOD_API_KEY = "<secret utilisateur>"
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\start-client-runpod-bench.ps1 `
  -BaseUrl "https://<endpoint>/v1" `
  -ModelId "<model-id>" `
  -Runtime "<runtime-servi>" `
  -RuntimeProfile "qwen-32b-q5-profile-1"
```

Variables disponibles :

- sélection : `SAAIA_LLM_PROVIDER_MODE=RunPodBench` et
  `SAAIA_LLM_EXTERNAL_POLICY=BenchmarkExternalAllowed` ;
- connexion : `SAAIA_RUNPOD_API_KEY`, `SAAIA_RUNPOD_BASE_URL`,
  `SAAIA_RUNPOD_MODEL`, `SAAIA_RUNPOD_REQUEST_TIMEOUT_SECONDS` ;
- identité : `SAAIA_RUNPOD_RUNTIME`, `SAAIA_RUNPOD_RUNTIME_PROFILE`,
  `SAAIA_RUNPOD_MODEL_PATH`, `SAAIA_RUNPOD_QUANTIZATION` ;
- runtime : `SAAIA_RUNPOD_CTX_SIZE`, `SAAIA_RUNPOD_BATCH_SIZE`,
  `SAAIA_RUNPOD_UBATCH_SIZE`, `SAAIA_RUNPOD_THREADS`,
  `SAAIA_RUNPOD_THREADS_BATCH`, `SAAIA_RUNPOD_GPU_LAYERS`,
  `SAAIA_RUNPOD_FLASH_ATTN`.

Ces paramètres décrivent le serveur de benchmark et apparaissent dans les
artefacts. Le démarrage effectif de llama-server sur le pod reste géré par le
script ou le template RunPod choisi, hors de la logique métier SAAIA.

Fermer le client termine le run SAAIA. L'endpoint public au token n'alloue pas
de pod privé à arrêter. Pour un futur endpoint privé, arrêter ensuite le worker
ou le pod depuis RunPod afin d'arrêter sa facturation. Une nouvelle console sans
ces variables, ou le mode `Local` explicite, désactive le benchmark côté client.

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

Le premier candidat A763 est l'endpoint public au token Qwen3 32B AWQ. Il ne
requiert ni pod privé ni location GPU horaire. La procédure est :

1. exécuter sans clé le préflight de
   `config/runpod-benchmark.a763.json` ;
2. après autorisation de sortie de contenu et de 5 USD maximum, créer ou ouvrir
   le compte RunPod, copier une clé limitée et l'importer avec
   `import-llm-secret-from-clipboard.ps1 -Provider RunPod` ;
3. lancer uniquement la sonde synthétique, limitée réellement à deux appels :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass `
     -File .\tools\test-runpod-campaign-profile.ps1 `
     -Execute -Stage Probe -ExternalContentAuthorized
   ```

4. rapprocher le modèle observé, le registre SAAIA et la consommation RunPod ;
5. si la sonde est valide, lancer `MealGrid` une fois avec le fichier
   d'environnement serveur, puis relire les vingt cellules et leurs preuves ;
6. si cette revue est acceptable, lancer `FullBank` avec
   `-FullBankAuthorized` sur le même commit et le même profil ;
7. conserver les résultats mécaniques et sémantiques séparément, vérifier les
   coûts et l'absence de processus SAAIA résiduel.

L'endpoint public n'expose pas le GPU, le hash des poids ou la révision du
runtime. Si sa qualité est insuffisante ou si une preuve matérielle est requise,
un endpoint Serverless privé devient une campagne distincte. Cette seconde
campagne doit sceller image, modèle, hash, quantification, GPU et paramètres,
puis arrêter le worker dès la fin pour arrêter la facturation.

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
`b20fcc2`. À ce stade historique du 12 septembre, la campagne complète 3/3
restait empêchée par le plafond Free de 50 requêtes par jour. Ce blocage ne
décrit plus le compte actuel : Tier 1 est vérifié le 13 septembre à 15:59 UTC,
avec 500 000 TPM, 500 RPM et 900 000 TPD pour Terra. Les derniers essais ont
effectivement été exécutés sur ce palier.

Le commit `4744d81` ferme aussi un écart de résilience du fournisseur avancé :
un timeout ou une rupture réseau pendant la lecture du corps HTTP est maintenant
normalisé en `advanced_llm_timeout` ou `advanced_llm_transport_error`. Une
annulation demandée par l'appelant reste une annulation. Les 33 tests ciblés et
les 2 150 tests backend Release passent ; une sonde live opt-in est ignorée.

Le commit `09207d6` ajoute au registre avancé un identifiant propre à chaque
appel, la corrélation du job, la durée, les tentatives et les retries. Le commit
`7614018` rend les deux indisponibilités compréhensibles dans WinUI tout en
masquant le payload fournisseur non validé. La validation Release exacte de ce
dernier SHA rapporte 10 tests contrats, 2 150 backend et 2 237 client réussis,
soit 4 397 réussites, zéro échec et deux probes live opt-in ignorées.

Restent obligatoires avant approbation :

- la banque Terra complète 3/3 sur un descendant corrigé, à code,
  configuration et corpus figés ; le palier payé est déjà disponible ;
- la validation qualitative et source par source de chaque répétition ;
- trois réussites consécutives du planning 5 × 4 sur état figé ;
- un test RunPod réel et la comparaison d'un modèle open-source ;
- la validation terminale WinUI actuelle d'une réponse avancée acceptée et de
  toutes ses cartes ; l'affichage réel de deux cartes et un clic exact ont déjà
  été démontrés historiquement sur un résultat durable rejoué, sans nouvel appel ;
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

## I. État mesuré du 13 septembre — A815 à A817

L'enveloppe autorisée est de 40 USD après dix USD achetés par l'utilisateur,
qui autorise l'usage de tous ses crédits achetés pour la mission. Aucun achat
par l'agent et aucune activation d'auto-reload. Billing affichait 10,08 USD
avant les nouveaux essais, à 15:59 UTC. Le journal après ces essais totalise
30,36662880 USD ; il reste 9,63337120 USD calculés. Cette dernière valeur est
une comptabilité locale, pas une nouvelle lecture du solde de facturation.

Sur `ad7fe14e`, A816 exécute réellement 31 recherches, dont des titres dans
leurs sources observées, et aucune lecture native. Sept appels coûtent
0,3669042 USD ; le résultat final reste une insuffisance avec un exemple,
sans les vingt cellules. Le planning E2E n'est donc pas validé.

A817 réutilise la même demande et exactement le même prompt système Writer,
mais fournit la projection de vingt lectures canoniques déjà retrouvées :
209 chunks, 58 preuves visibles. Un seul appel réel coûte 0,0768015 USD et
prend 20,223 s. Il produit vingt choix distincts et ordinaires pour les quatre
créneaux. La revue humaine de leurs 23 références et le parseur/policy de
production passent. Les placements sont annoncés comme une proposition ; les
sources ne doivent pas avoir prescrit ces placements. Les recettes restent
reliées à leurs propres contenus, parfois par heading et corps voisins.

Ce résultat prouve une capacité de synthèse sur ce diagnostic connu. Les
lectures sont choisies extérieurement, dont deux coordonnées manuellement ;
aucun critique, job durable, clic WinUI ni trois répétitions n'est exécuté
dans A817. Il ne valide pas le RAG autonome et ne remplace pas un oracle complet
ou le holdout. Parmi les 23 références exactes utilisées dans ce diagnostic,
huit avaient été retournées par A816 et trois étaient visibles à son Writer
final ; des preuves alternatives peuvent avoir existé. Il faut comparer
l'exploration documentaire et la conservation des preuves utiles avant de
multiplier les corrections de consignes.

Le garde des campagnes fondé sur `user_id='automated-validation'` ne couvre
pas les GUID utilisateurs réellement générés par le client. L'inventaire
complémentaire de 306 UUID connus du journal retrouve 295 jobs et aucun
non-terminal ; onze UUID manquent, dont la corrélation du diagnostic Writer
sans job durable. Ce contrôle en lecture seule ne couvre pas les jobs sans
appel enregistré ; corriger le garde avant une prochaine campagne aveugle.

Preuves : `audit-de-cloture-du-goal-a815-2026-09-13.md`,
`navigation-autonome-et-redaction-isolee-a816-a817-2026-09-13.md` et leurs
assessments/empreintes. Le verdict aveugle historique BH6 reste rejeté à 1/24,
les installateurs interactifs restent différés, RunPod reste non financé et
le produit reste **TESTE_NON_APPROUVE**.

### I.9 — Garde de propriété corrigé pour les nouvelles campagnes (A818)

Les GUID utilisateurs réels sont maintenant enregistrés avant toute requête
du client de test, puis utilisés pour la clôture de la campagne. Le contrôle
par libellé reste en lecture seule et une portée vide est explicitement
distinguée d'un audit global. Les trois tests PostgreSQL réels et les suites
client/backend passent ; aucun comportement sémantique du produit ni appel
API n'est changé. Voir `propriete-des-jobs-de-validation-a818-2026-09-13.md`.
Cette annotation clôt la correction du garde pour les futurs runners source ;
elle ne requalifie pas les anciennes captures ni le planning autonome.

## J. État du 18 septembre — Candidate Explorer et budget opérationnellement épuisé

Cette section actualise la prochaine séquence expérimentale sans effacer les
mesures historiques ci-dessus.

Le registre local réconcilié contient 1 127 écritures et totalise
39,99436060 USD sur l'enveloppe autorisée de 40 USD. La marge calculée est de
0,00563940 USD. Le profil gelé doit réserver au minimum 0,00614650 USD pour son
premier appel Planner ; la campagne est donc
`PREREGISTERED_BUDGET_BLOCKED`. Le solde résiduel ne doit pas être utilisé en
abaissant artificiellement les tokens du Planner, car cela changerait
l'expérience et mesurerait une troncature prévisible. Aucun achat, auto-reload
ou location GPU n'est autorisé par ce constat.

### J.1 — Architecture désormais prête pour la prochaine mesure

A857 à A860 ont ajouté un inventaire de candidats généraliste, son checkpoint
PostgreSQL reprenable, une phase Candidate Explorer distincte du Writer et la
couverture des grilles comme des collections plates. Le modèle décide toujours
de la pertinence ; le code vérifie identités, états, preuves, comptes, bornes et
transitions.

A861 gèle un unique pilote Terra du planning 5 × 4 : Responses, topologie agent,
espace de travail natif, historique de 32 768 caractères, 4 096 tokens pour
Explorer, Writer et Critic, sept appels maximum et une répétition. Les wrappers
OpenAI et RunPod transmettent le même contrat au runner commun.

A862 à A864 ferment la chaîne de preuve de ce pilote :

- traces brutes fournisseur dans un répertoire privé ;
- audit privé et borné du job, du checkpoint et des événements d'outils ;
- empreintes SHA-256 et manifeste de traces, y compris après interruption ;
- vérification automatique des schémas, comptes, chemins, empreintes, jobs et
  rôles Explorer/Writer/Critic ;
- annexe privée lisible qui rapproche candidats, rôles, EvidenceId, recherches,
  réponse et textes canoniques ;
- rapport public limité aux comptes, rôles techniques, verdicts et empreintes.

La suite backend A862 compte 2 459 réussites, zéro échec et trois live ignorés.
Les cinq fixtures d'intégrité A863 réussissent. Ces résultats valident le
harnais, pas l'autonomie sémantique de Terra.

### J.2 — Séquence proposée lorsque du calcul redevient disponible

1. Réenregistrer uniquement l'enveloppe réellement autorisée et observer le
   tier fournisseur juste avant l'essai. Ne modifier ni corpus, ni banque, ni
   paramètres sémantiques.
2. Exécuter un seul run du profil gelé. Ne pas lancer immédiatement un lot ou
   une campagne 3/3.
3. Exécuter l'assessment mécanique, la vérification d'intégrité A863 et le paquet
   humain A864.
4. Examiner les vingt cellules et leurs sources, mais aussi l'inventaire : titres
   découverts, corps vérifiés, lacunes par rôle, recherches sans rendement et
   pertes éventuelles entre Explorer et Writer.
5. Si le run échoue, corriger uniquement l'étape démontrée fautive puis geler un
   nouveau profil. Un défaut de recherche ne devient pas une limite de synthèse,
   et un défaut Writer ne justifie pas davantage d'appels Explorer.
6. Si le run réussit mécaniquement et sémantiquement, exécuter les répétitions
   deux et trois sur le même état, puis un nouveau holdout aveugle.
7. Valider enfin le basculement local-vers-avancé et les cartes source dans
   WinUI avant toute approbation produit.

### J.3 — Choix de l'hébergement après preuve fonctionnelle

La localisation du grand modèle ne change pas le contrat RAG. La sélection doit
venir après la première preuve fonctionnelle et suivre trois niveaux :

1. **OpenAI Terra** reste le prototype DEV de référence pour savoir si le contrat
   agentique fonctionne avec un modèle suffisamment capable.
2. **RunPod ou une location GPU comparable** sert ensuite de BENCH temporaire
   pour choisir un modèle ouvert et mesurer VRAM, contexte réellement utile,
   latence, concurrence et coût horaire sur les mêmes cas gelés.
3. **Serveur client on-premise** reste la cible de production avancée. Le modèle,
   le runtime et le dimensionnement ne sont retenus qu'après reproduction de la
   qualité et mesure de la charge simultanée attendue.

Il serait prématuré de construire maintenant les choix interactifs complets de
licence et d'installateur. Le code doit continuer à exposer une capacité locale,
une capacité avancée et une configuration de provider indépendante. Lorsque le
modèle serveur est qualifié, la licence pourra autoriser local seul, avancé
seul, combinaison des deux ou endpoint externe approuvé, et l'installateur
matérialisera URL, secret, téléchargement, préflight matériel et politique de
transmission.

Le point de reprise n'est donc plus une nouvelle variante de prompt. C'est
l'unique pilote A861, suivi de la chaîne de preuve A862–A864. Tant que ce pilote
n'est pas exécuté et revu, le planning, la capacité avancée et le produit
restent `TESTE_NON_APPROUVE`.
