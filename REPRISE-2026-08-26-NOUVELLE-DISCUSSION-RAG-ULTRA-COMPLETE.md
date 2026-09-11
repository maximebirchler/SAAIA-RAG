# Reprise ultra complète — SAAIA RAG

> Date de vérification : 2026-08-26, Europe/Zurich  
> Dépôt : `C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG`  
> Branche : `SAAIA_V3.1`  
> Commit de base : `5f35881cdc67d12a076fcd2a7a1004656ac9a37a`  
> Objet : donner à une nouvelle discussion Codex toutes les informations
> techniques, opérationnelles et méthodologiques nécessaires pour reprendre le
> chantier sans redécouverte, sans exposition de secrets et sans répéter les
> erreurs des précédents essais.

## 0. Règle de sécurité de ce rapport

Ce fichier ne contient volontairement **aucun mot de passe, aucune clé API,
aucun pepper et aucune clé privée en clair**. Il donne leur emplacement exact,
leur nom de variable et les commandes permettant de les charger sans les
afficher.

Cette décision est importante : ce rapport est à la racine d'un dépôt Git et
peut être commité. Copier ici le contenu de `infra/.env.server-linux`, de
`secure.json` ou de la clé de signature créerait immédiatement une fuite de
secrets dans l'historique Git, les outils de revue, les sauvegardes et
potentiellement Telegram. Une nouvelle discussion a néanmoins tout ce qu'il
faut pour travailler sans demander de mot de passe à l'utilisateur.

Les sources de vérité sensibles sont :

- `C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG\infra\.env.server-linux` ;
- `C:\Users\MBirchler\AppData\Local\SAAIA\client\secure.json`, protégé par
  DPAPI pour l'utilisateur Windows courant ;
- `C:\Users\MBirchler\.saaia\keys\saaia_config_private.b64`, clé privée de
  signature de configuration ;
- `C:\Users\MBirchler\.ssh\id_ed25519_saaia`, identité SSH.

Ne jamais afficher leur contenu dans une réponse, une trace, un rapport, une
commande Telegram ou un artefact de test. Ne jamais les ajouter à Git.

## 1. Résumé exécutif à lire avant toute action

Le chantier vise un RAG générique où **Qwen3 côté client est l'orchestrateur et
le décideur sémantique principal**. Le code doit assurer les faits mécaniques :
schémas, sécurité, budgets, provenance, identité des preuves, déduplication
canonique, pagination, résolution fichier/page et réparation de protocole. Il
ne doit pas décider à la place du LLM qu'une recette, une catégorie ou une
source est sémantiquement appropriée.

La refonte est avancée mais non terminée :

- l'ingestion canonique des dix PDF Cuisine est validée ;
- le backend distant est sain et expose les primitives documentaires ;
- le nouveau runner `SourceBackedRag` est largement modularisé ;
- la provenance canonique et les cartes de sources existent ;
- les tests déterministes couvrent beaucoup de contrats ;
- **aucun live final du planning de repas n'est encore vert, rapide et
  professionnel** ;
- **la mémoire projet M2 et le ContextEnvelope restent essentiellement une
  décision d'architecture, pas une fonctionnalité complète** ;
- `RagEndpoints.cs` reste un monolithe de 36 534 lignes ;
- le répertoire `ToolAgent` reste très volumineux et conserve beaucoup de
  logique historique à caractériser puis supprimer ;
- le dépôt est très sale et ne doit surtout pas être nettoyé aveuglément.

Le dernier live significatif du 2026-08-07 a expiré après douze minutes avant
le writer. Il avait matérialisé 157 éléments, audité 100 candidats, accepté 24
et rejeté 76, puis il a été annulé dans
`CompleteCandidateColumnCompatibilityAsync`. Le problème dominant est donc une
combinaison de compréhension initiale imparfaite, sur-orchestration, trop
d'appels/passes sémantiques et contexte 4K constamment presque plein — pas une
absence de données dans le backend.

Point discriminant découvert dans la trace : Qwen3 a défini l'objet atomique
comme `Produit alimentaire`, avec le brouillon hypothétique
`Pain au chocolat au lait`, alors que le livrable attend des propositions de
repas/recettes nommées. Malgré la consigne indiquant que ce brouillon était
éphémère, une étape ultérieure a réellement lancé une navigation
`title_anchor` sur `Pain au chocolat au lait`. Il y a donc une **fuite d'une
hypothèse de raisonnement vers la recherche documentaire**. Ajouter un filtre
Cuisine en code ne serait pas la bonne correction ; il faut réparer le contrat
sémantique et la continuité d'état de façon générique.

## 2. Intention produit non négociable

### 2.1 Le LLM client doit rester le décideur sémantique

Le LLM décide :

- ce que demande réellement l'utilisateur ;
- si une clarification est matériellement nécessaire ;
- la forme du livrable ;
- le type de preuves à rechercher ;
- le périmètre documentaire ;
- les outils utiles et leur ordre ;
- les requêtes ;
- la pertinence, la complémentarité et la diversité des candidats ;
- le moment où les preuves sont suffisantes ;
- l'affectation des preuves aux éléments du livrable ;
- la rédaction et l'explication d'une insuffisance.

Le code peut borner le temps, la mémoire, le nombre maximal de tours et les
tailles de payload pour protéger la machine. Ces bornes sont des limites
mécaniques, pas un scénario imposant « exactement quatre appels » ou « toujours
utiliser tel outil ».

### 2.2 Le backend LLM n'est pas l'orchestrateur de conversation

Le conteneur serveur `saaia-llama` utilise actuellement Qwen3 pour des tâches de
backoffice, de résumé ou d'enrichissement. Il ne doit pas remplacer le Qwen3
client dans le chemin de réponse utilisateur. Une future utilisation du LLM
serveur pour améliorer l'ingestion est autorisée, mais elle doit rester
traçable, optionnelle et hors du chemin conversationnel principal.

### 2.3 Aucune logique produit spécialisée Cuisine

Cuisine est le corpus prioritaire de validation parce qu'il est difficile et
contient scans, tableaux, listes, images et structures variées. Le code produit
ne doit toutefois contenir aucun nom de recette, de document Cuisine, de jour,
de créneau de repas ni de catégorie comme règle décisionnelle.

Les exemples dans les tests sont admis. Les branches de production qui disent
en substance « si repas alors recette » ou « si petit-déjeuner alors… » ne le
sont pas.

### 2.4 Une réponse rapide reste un critère de qualité

Une réponse correcte en huit ou douze minutes n'est pas acceptable sur la
machine de référence. Une question simple doit éviter les passes de
planification, d'audit et de réparation inutiles. Une demande complexe peut
nécessiter plus de recherche, mais chaque appel LLM doit avoir une utilité
mesurée.

Cibles de conception à valider, puis à ajuster par mesure :

- question documentaire simple : premier contenu utile en moins de 15 s,
  réponse finale idéalement en moins de 30 s ;
- document nommé/navigation : réponse en moins de 45 s hors téléchargement ;
- planning 5 × 4 : viser moins de 120 s, avec plafond expérimental de 180 s ;
- toute régression au-delà doit produire une ventilation par étape, tokens,
  appels, cache et outils — jamais être masquée en augmentant seulement le
  timeout.

Ces chiffres sont des objectifs, pas des assertions sur l'état actuel.

## 3. État Git exact au début de ce rapport

État vérifié le 2026-08-26 avant l'ajout du présent fichier :

- branche : `SAAIA_V3.1` ;
- HEAD : `5f35881cdc67d12a076fcd2a7a1004656ac9a37a` ;
- divergence par rapport à `origin/SAAIA_V3.1` : 15 commits locaux d'avance,
  0 en retard ;
- 468 entrées dans `git status --porcelain=v1` ;
- 66 fichiers modifiés ;
- 8 fichiers suivis supprimés ;
- 394 entrées non suivies ;
- diff suivi : 8 863 insertions et 86 512 suppressions sur 74 fichiers ;
- `git diff --check` ne signalait pas d'erreur de contenu, seulement des
  avertissements de normalisation LF/CRLF.

Le présent rapport ajoute une entrée non suivie supplémentaire tant qu'il
n'est pas commité.

### 3.1 Conséquence opérationnelle

Ne jamais exécuter :

- `git reset --hard` ;
- `git clean -fd` ou une variante ;
- une suppression globale des fichiers non suivis ;
- `git checkout -- .` ;
- un script de formatage massif sans inventaire préalable.

Une grande partie de la refonte moderne se trouve dans les fichiers non
suivis. Les supprimer reviendrait à perdre le travail. Les suppressions de
gros fichiers historiques sont également intentionnelles mais ne doivent être
commitées qu'après validation de l'équivalence fonctionnelle.

Éviter `git log --all` et `git fsck` comme diagnostic de routine : des refs
internes Codex peuvent être invalides et produire du bruit sans lien avec le
worktree. Utiliser plutôt :

```powershell
git branch --show-current
git rev-parse HEAD
git rev-list --left-right --count HEAD...origin/SAAIA_V3.1
git status --short
git diff --stat
git diff --numstat
git diff --check
```

### 3.2 Déploiement depuis un worktree sale

`infra/scripts/prod/deploy-remote-backend.ps1` calcule une révision à partir du
commit **et du hash des sources suivies/non suivies pertinentes**. Un backend
peut donc être déployé avec du code non commité. Le `/ready` actuel expose :

`5f35881cdc67d12a076fcd2a7a1004656ac9a37a-source-776a27a1fbf75c5ce2e139a4fdc0cb201ae37ca38af7c9168eac59f1d2b7a03d`

Ne jamais conclure que « le serveur exécute HEAD » en comparant seulement les
40 caractères du commit. Il faut comparer la révision complète ou redéployer
le snapshot de source voulu.

## 4. Documents à lire dans une nouvelle discussion

Ordre recommandé, afin de ne pas noyer le contexte :

1. ce rapport ;
2. `GOAL-2026-07-28-RAG-CANONIQUE-LLM-FIRST.md` ;
3. `ADR-2026-07-08-rag-llm-orchestration-source-backed.md` ;
4. `ADR-2026-07-28-project-memory-and-context-envelope.md` ;
5. `AUDIT-2026-07-28-CUISINE-CANONICAL-REINGESTION.md` ;
6. `REPRISE-2026-08-03-batched-candidate-audit-checkpoint.md` ;
7. `ANALYSE-2026-07-29-LATENCE-RAG-SIMPLE.md` ;
8. `AUDIT-RAG-QWEN3-2026-07-25.md` ;
9. seulement pour une enquête historique :
   `TODO-2026-07-16-RAG-LLM-ORCHESTRATION.md`.

Le CDC de référence n'est actuellement pas présent sous
`documents/cdc/` dans le dépôt. La copie locale vérifiée est :

`C:\Users\MBirchler\Desktop\ecom\SAAIA\CDC\RAG\CDC Agent AI – RAG - V3.1.md`

Autres archives de reprise encore disponibles :

- `C:\Users\MBirchler\Downloads\Rapport de reprise du 16.07.26.txt` — environ
  5,1 Mo, historique très détaillé ;
- `C:\Users\MBirchler\.codex\attachments\2920c311-19db-4a00-beaf-c6e3e7c9249d\pasted-text.txt`.

Ne pas injecter le rapport de 5 Mo en entier dans un prompt LLM. Y rechercher
une erreur, un artefact ou une décision précise avec `rg`.

## 5. Accès serveur et exploitation

### 5.1 Accès vérifiés

| Ressource | Valeur non sensible | Authentification |
|---|---|---|
| Alias SSH | `saaia-server` | clé Ed25519 |
| Hôte Tailscale SSH | `100.80.213.61` | réseau Tailnet + clé |
| Utilisateur SSH | `maxime` | aucune saisie de mot de passe constatée |
| Port SSH | `22` | `IdentitiesOnly yes` |
| Clé SSH | `~/.ssh/id_ed25519_saaia` | ne jamais afficher/copier |
| Backend HTTPS | `https://saaia-server.taila2196b.ts.net` | `X-Api-Key` pour les routes protégées |
| Backend local serveur | `http://127.0.0.1:5122` | selon route |
| Racine installation | `/opt/saaia` | permissions utilisateur/groupe serveur |
| Répertoire de déploiement | `/opt/saaia/deploy` | via SSH |
| PostgreSQL | base `saaia`, utilisateur `saaia-admin` | `POSTGRES_PASSWORD` dans l'env secret |
| Qdrant | port hôte `6333` | `QDRANT_API_KEY` obligatoire |
| TEI embeddings | port hôte `8081` | réseau serveur |
| LLM backoffice | `http://saaia-llama:8080` dans Docker | réseau `infra_default` |

Connexion simple :

```powershell
ssh -o BatchMode=yes -o ConnectTimeout=8 saaia-server
```

Le mode `BatchMode=yes` est utile en automatisation : il échoue immédiatement
si la clé n'est plus utilisable au lieu d'attendre un mot de passe.

### 5.2 Secrets serveur disponibles

Le fichier `infra/.env.server-linux` contient notamment :

- `POSTGRES_PASSWORD` ;
- `QDRANT_API_KEY` ;
- `SAAIA_AUTH_PEPPER` ;
- `SAAIA_BOOTSTRAP_API_KEY` ;
- `SAAIA_CONFIG_PRIVATE_KEY_PATH` ;
- les réglages TEI, reranker, ingestion, Docling/OCR et backoffice.

Il est ignoré par `.gitignore`. Le chemin de clé de signature configuré est
`C:\Users\MBirchler\.saaia\keys\saaia_config_private.b64` et le fichier existe.

Pour charger la clé API de test dans **le processus PowerShell courant** sans
l'imprimer :

```powershell
$envFile = Resolve-Path .\infra\.env.server-linux
$serverEnv = @{}
foreach ($line in [IO.File]::ReadAllLines($envFile, [Text.Encoding]::UTF8)) {
    if ($line -match '^\s*#' -or $line -notmatch '=') { continue }
    $i = $line.IndexOf('=')
    $serverEnv[$line.Substring(0, $i).Trim()] = $line.Substring($i + 1).Trim()
}
$env:SAAIA_API_KEY = $serverEnv['SAAIA_BOOTSTRAP_API_KEY']
$env:SAAIA_VALIDATION_BACKEND_URL = 'https://saaia-server.taila2196b.ts.net'
Remove-Variable serverEnv
```

Alternative pour les tests exécutés sous le même utilisateur Windows :
`SecureLocalStore.GetServerApiKey()` lit la clé depuis
`%LOCALAPPDATA%\SAAIA\client\secure.json` via DPAPI. L'environnement explicite
reste préférable pour un run reproductible.

Après les tests, retirer la variable de la session :

```powershell
Remove-Item Env:SAAIA_API_KEY -ErrorAction SilentlyContinue
```

### 5.3 Santé serveur vérifiée le 2026-08-26

Commande :

```powershell
Invoke-RestMethod 'https://saaia-server.taila2196b.ts.net/ready' -TimeoutSec 15 |
    ConvertTo-Json -Depth 8
```

État observé :

- `ok=true` ;
- PostgreSQL prêt ;
- Qdrant prêt, authentification API key configurée et requise ;
- TEI prêt, embeddings 768 dimensions ;
- modèle embeddings : `intfloat/multilingual-e5-base` ;
- reranker actif : `Alibaba-NLP/gte-multilingual-reranker-base`, révision
  `52ac27fc088c42ad6b4371003e4c0b00d6c11282` ;
- LLM backoffice sain :
  `Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf` ;
- provenance canonique `ready` ;
- configuration signée présente et vérifiée ;
- Docling prêt : `docling-serve-1.27.0`, `docling-slim-2.113.0`, extension
  `saaia-structure-2` ;
- Docling utilise actuellement le **CPU**, 8 threads, 1 worker et 1 conversion
  simultanée ;
- OCR déclaré comme fourni par Document Intelligence ;
- recherche RAG : 4 slots, file de 16, 0 requête active et 0 en attente au
  moment du contrôle ;
- ingestion : 2 workers, concurrence TEI 2, Qdrant 4, OCR 1, calcul lourd 1 ;
- batch embeddings configuré à 32 avec retry adaptatif.

Le fait que le serveur dispose d'un GPU ne signifie donc pas que toute
l'ingestion l'utilise. Docling tourne actuellement sur CPU. Avant de déplacer
Docling/OCR sur GPU, mesurer sur les PDF réels : fidélité de structure, débit,
VRAM, concurrence avec TEI/reranker/LLM et comportement de repli. Ne pas changer
le device sur la seule intuition « GPU = plus rapide ».

### 5.4 Conteneurs actifs vérifiés

Les conteneurs SAAIA observés sont :

- `infra-backend-1` ;
- `infra-docling-1` ;
- `infra-postgres-1` ;
- `infra-qdrant-1` ;
- `infra-tei-1` ;
- `infra-tei-rerank-1` ;
- `saaia-llama`.

Commandes de diagnostic sans mutation :

```powershell
ssh saaia-server "docker ps --format '{{.Names}}|{{.Status}}' | sort"
ssh saaia-server "docker logs --tail 250 infra-backend-1"
ssh saaia-server "docker logs --tail 250 infra-docling-1"
ssh saaia-server "docker logs --tail 250 infra-tei-rerank-1"
ssh saaia-server "curl -fsS http://127.0.0.1:5122/ready"
```

Les logs peuvent contenir des questions ou extraits de documents. Ne pas les
publier intégralement sans assainissement.

### 5.5 Déploiement backend

La commande recommandée depuis la racine du dépôt est :

```powershell
.\infra\scripts\prod\deploy-remote-backend.ps1 -RemoteSourceBuild
```

Le script connaît déjà par défaut :

- serveur `maxime@100.80.213.61` ;
- contexte Docker `saaia-server` ;
- compose `infra/docker-compose.prod.yml` ;
- env `infra/.env.server-linux` ;
- répertoire de staging local `out/remote-deploy`.

Dans le shell contrôlé le 2026-08-26, la commande `docker` n'est pas dans le
PATH. Le script détecte cette absence et bascule automatiquement en build
source distant, mais passer explicitement `-RemoteSourceBuild` rend
l'intention claire. Il utilise `ssh` et `scp`, crée une archive des sources,
copie temporairement l'env sur le serveur en mode `0600`, construit l'image,
synchronise la configuration signée, redémarre les services nécessaires et
contrôle `/ready`.

Options à utiliser avec prudence :

- `-WithDependencies` redéploie backend, PostgreSQL, Qdrant, TEI, reranker et
  Docling ; ne l'utiliser que si les dépendances ont réellement changé ;
- `-NoCache` force le rebuild backend ; utile pour diagnostiquer un cache
  invalide, coûteux sinon ;
- `-SkipConfigSync` peut laisser serveur et code désalignés ;
- `-SkipReadyCheck` retire une preuve essentielle ;
- `-SkipRemoteProvision` suppose que les répertoires et permissions sont déjà
  corrects.

Après un déploiement, archiver au minimum : commande exacte, branche, commit,
hash source, sortie compose, `/ready`, date et tests pré-déploiement.

### 5.6 Pièges des scripts d'exploitation

- `infra/scripts/prod/diag.ps1` utilise par défaut `infra/.env` et le Docker
  local. Il ne faut pas le présenter comme diagnostic du serveur distant sans
  adapter explicitement le contexte.
- `infra/scripts/prod/backup.ps1` utilise également le Docker local par défaut.
- `infra/scripts/prod/restore.ps1` exécute notamment
  `docker compose down -v`. C'est une opération destructrice sur les volumes :
  ne jamais la lancer pour « voir si cela marche ».
- `infra/scripts/prod/smoke.ps1` cible `http://localhost:5122` par défaut et son
  test de rate limit peut effectuer jusqu'à 600 tentatives. Pour un smoke de
  production normal, passer l'URL et utiliser `-SkipRateLimit` sauf si le rate
  limiting est précisément l'objet du test.

Exemple de smoke non agressif :

```powershell
.\infra\scripts\prod\smoke.ps1 `
    -BaseUrl 'https://saaia-server.taila2196b.ts.net' `
    -ApiKey $env:SAAIA_API_KEY `
    -SkipRateLimit
```

### 5.7 Stockage distant et anciens builds

Le volume racine du serveur était à 63 % : 280 Go utilisés sur 466 Go, 166 Go
libres. `/opt/saaia/deploy` occupe environ 1,9 Go. Treize répertoires
`backend-build-*` existent ; les snapshots de source visibles font environ
5,4 à 5,5 Mo chacun. Ne pas les supprimer au hasard : identifier d'abord ce
qui occupe le reste du répertoire, le build réellement actif et la politique
de rollback. Ce nettoyage n'est pas un blocage immédiat.

## 6. Runtime LLM client et machine de référence

### 6.1 Fichiers actuels

Configuration fallback non packagée :

`C:\Users\MBirchler\AppData\Local\SAAIA\client\settings.json`

Modèle :

`C:\Users\MBirchler\AppData\Local\SAAIA\Models\Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf`

Taille observée : 2 889 513 696 octets.

Runtime CUDA qualifié/configuré :

`C:\Users\MBirchler\AppData\Local\SAAIA\llm\runtime\win-cuda-x64\b10098\llama-server.exe`

Réglages actuellement persistés dans le fallback JSON :

```text
--ctx-size 4096 -t 4 -b 512 -ngl 65 --ubatch-size 128
--threads-batch 4 --flash-attn on --jinja
```

Backend préféré :
`https://saaia-server.taila2196b.ts.net`.

Le serveur local n'était pas actif sur les ports 1234 ou 12661 au moment du
contrôle. Aucun processus `llama-server` n'était en cours.

Attention : `AppSettings` essaie d'abord les `ApplicationData.LocalSettings`
du package WinUI, puis le JSON fallback si l'identité package est absente. Le
fichier JSON n'est donc pas nécessairement la seule source de vérité pour une
application packagée. Les tests headless utilisent normalement le fallback.

### 6.2 Matériel de référence

Baseline historique à revalider avec le probe matériel :

- NVIDIA Quadro P520, 4 Go de VRAM dédiée ;
- Intel UHD Graphics avec mémoire système partagée ;
- environ 32 Go de RAM système.

La mémoire partagée affichée par Windows pour l'Intel UHD n'est pas 16 Go de
VRAM dédiée et ne s'additionne pas simplement aux 4 Go NVIDIA. Le client
possède des voies CUDA, Vulkan, SYCL et CPU ; il faut mesurer chaque backend
applicable sur la machine puis promouvoir le meilleur profil, pas répartir un
même modèle entre Intel et NVIDIA par heuristique improvisée.

Le Q5_K_M a été retenu pour la qualité malgré un débit historique d'environ
7 tokens/s. Le Q4_K_M était plus rapide mais avait échoué sur des contrats
sémantiques importants. Toute nouvelle quantification doit être comparée sur
les mêmes scénarios de tool calling, structured output, retrieval et rédaction
— pas seulement sur tok/s.

### 6.3 Démarrer llama-server sans fenêtre

```powershell
$runtime = "$env:LOCALAPPDATA\SAAIA\llm\runtime\win-cuda-x64\b10098\llama-server.exe"
$model = "$env:LOCALAPPDATA\SAAIA\Models\Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf"
$out = Join-Path $PWD ("artifacts\local-llm\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))

.\tools\start-local-model-server.ps1 `
    -ServerPath $runtime `
    -ModelPath $model `
    -OutputDirectory $out `
    -Port 1234 `
    -ContextSize 4096 `
    -BatchSize 512 `
    -UbatchSize 128 `
    -Threads 4 `
    -GpuLayers 65 `
    -FlashAttention on `
    -CacheTypeK f16 `
    -CacheTypeV f16 `
    -Parallel 1 `
    -Device CUDA0 `
    -Jinja
```

Le script utilise `Start-Process -WindowStyle Hidden`, redirige stdout/stderr,
écrit `server.pid` et `server-profile.json`, puis attend `/health` et
`/v1/models`.

Vérification :

```powershell
Invoke-RestMethod 'http://127.0.0.1:1234/health'
Invoke-RestMethod 'http://127.0.0.1:1234/v1/models' | ConvertTo-Json -Depth 5
Invoke-RestMethod 'http://127.0.0.1:1234/props' | ConvertTo-Json -Depth 8
```

`/props` doit confirmer la taille réelle du contexte et le template de chat.

Arrêt propre après le test :

```powershell
$pidPath = Join-Path $out 'server.pid'
$pidValue = [int](Get-Content -LiteralPath $pidPath -Encoding ascii)
$process = Get-Process -Id $pidValue -ErrorAction Stop
if ($process.Path -like '*llama-server.exe') {
    Stop-Process -Id $pidValue
}
```

Ne pas tuer tous les processus par nom si un autre test ou une autre
application utilise llama-server.

## 7. Tester le client efficacement sans ouvrir WinUI

Le projet de tests
`client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj`
instancie directement `RagChatAgent`, `ToolAgentOrchestrator`, `ApiClient` et
`OpenAiLlmClient`. C'est le chemin recommandé pour presque toute la mise au
point : il exécute la logique réelle du client sans afficher `MainWindow`.

La fenêtre WinUI ne doit être lancée qu'après les validations headless, pour
la dernière preuve humaine de rendu, clic et navigation fichier/page.

### 7.1 Préparer une session de test

```powershell
$env:SAAIA_VALIDATION_BACKEND_URL = 'https://saaia-server.taila2196b.ts.net'
$env:SAAIA_VALIDATION_LLM_BASE_URL = 'http://127.0.0.1:1234/v1'
$env:SAAIA_VALIDATION_LLM_MODEL = 'Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf'
$env:SAAIA_VALIDATION_MANAGE_LOCAL_LLM_PROCESS = '0'
```

Charger `SAAIA_API_KEY` avec la méthode sûre de la section 5.2. Ne pas mettre
la valeur littérale dans l'historique PowerShell ou un script suivi.

Vérifier avant tout live :

```powershell
Invoke-RestMethod "$env:SAAIA_VALIDATION_BACKEND_URL/ready" -TimeoutSec 15
Invoke-RestMethod 'http://127.0.0.1:1234/health' -TimeoutSec 5
```

### 7.2 Compiler une fois

Utiliser x64, Debug, un seul build worker et désactiver le compilateur partagé
si le worktree a connu des verrous ou incohérences :

```powershell
dotnet build .\client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj `
    -p:Platform=x64 `
    -p:Configuration=Debug `
    -p:UseSharedCompilation=false `
    -nr:false `
    -m:1 `
    --no-restore
```

Si les assets NuGet sont absents, faire un `dotnet restore` explicite avant de
relancer. Ne pas alterner AnyCPU, x86, Release et x64 pendant un même
diagnostic : cela multiplie les assemblies et peut faire exécuter un binaire
ancien.

### 7.3 Échelle de test recommandée

Toujours monter progressivement :

1. tests déterministes de la classe ou du contrat modifié ;
2. probe backend sans LLM ;
3. microprobe LLM isolé ;
4. test live d'une question simple ;
5. planning complet ;
6. validation humaine WinUI finale.

Un test complet de douze minutes n'est pas un bon premier signal pour une
modification de contrat de 30 lignes.

### 7.4 Tests déterministes

Exemple ciblé :

```powershell
dotnet test .\client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj `
    -p:Platform=x64 `
    -p:Configuration=Debug `
    -p:UseSharedCompilation=false `
    --no-build `
    --no-restore `
    --filter "FullyQualifiedName~SourceBackedAgentV2Tests|FullyQualifiedName~OpenAiLlmClientTests|FullyQualifiedName~ApiClientDocumentsTransitionTests" `
    --logger "console;verbosity=minimal"
```

Dernières preuves historiques :

- 140/140 tests ciblés verts en 2 s dans
  `artifacts/validation-20260807-current/targeted-tests-104548.out.log` ;
- une suite plus large avait atteint 1 235/1 235, mais ce nombre n'a pas été
  rejoué le 2026-08-26 et doit être considéré comme historique.

Un test déterministe vert prouve un contrat codé, pas la qualité sémantique de
Qwen3 ni l'alignement réel d'une source.

### 7.5 Probes backend rapides sans LLM

Inventaire des cartes :

```powershell
$env:SAAIA_LIVE_CONTENT_CARD_INVENTORY_PROBE = '1'
dotnet test .\client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj `
    -p:Platform=x64 -p:Configuration=Debug --no-build --no-restore `
    --filter "FullyQualifiedName=SAAIA.Client.ToolAgent.Tests.LiveContentCardInventoryProbeTests.Live_cuisine_content_card_inventory_is_paged_citable_and_materialized"
Remove-Item Env:SAAIA_LIVE_CONTENT_CARD_INVENTORY_PROBE
```

Navigation documentaire :

```powershell
$env:SAAIA_LIVE_DOCUMENT_NAVIGATION_PROBE = '1'
dotnet test .\client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj `
    -p:Platform=x64 -p:Configuration=Debug --no-build --no-restore `
    --filter "FullyQualifiedName=SAAIA.Client.ToolAgent.Tests.LiveDocumentNavigationProbeTests.Live_cuisine_navigation_exposes_named_recipe_anchors_when_enabled"
Remove-Item Env:SAAIA_LIVE_DOCUMENT_NAVIGATION_PROBE
```

Inventaire retrieval brut :

```powershell
$env:SAAIA_LIVE_RETRIEVAL_INVENTORY_PROBE = '1'
$env:SAAIA_RETRIEVAL_INVENTORY_CATEGORY = 'Cuisine'
dotnet test .\client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj `
    -p:Platform=x64 -p:Configuration=Debug --no-build --no-restore `
    --filter "FullyQualifiedName=SAAIA.Client.ToolAgent.Tests.LiveRagRetrievalInventoryProbeTests.Live_meal_plan_queries_capture_raw_hits_and_content_cards_when_enabled"
Remove-Item Env:SAAIA_LIVE_RETRIEVAL_INVENTORY_PROBE
Remove-Item Env:SAAIA_RETRIEVAL_INVENTORY_CATEGORY
```

Ces probes séparent les défauts backend/provenance du comportement LLM.

### 7.6 Microprobes LLM avant un full live

Les probes les plus utiles pour le verrou actuel sont :

- `LiveNativeRouterSemanticContractTests` — clarification, forme et première
  action ;
- `LiveSourceBackedAgentV2ToolChoiceProbeTests` — choix du premier outil ;
- `LiveSourceBackedSemanticGranularityMicroprobeTests` — définition de l'objet,
  résolution des libellés, audit groupé et affectation ;
- `LiveRealContentCardDirectAuditProbeTests` — audit de vraies cartes serveur ;
- `LiveSourceBackedCandidateRecoveryMicroprobeTests` — décision de continuer
  une pagination ou de pivoter.

Exemple pour la récupération/pagination :

```powershell
$env:SAAIA_LIVE_SOURCE_BACKED_CANDIDATE_RECOVERY_PROBE = '1'
dotnet test .\client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj `
    -p:Platform=x64 -p:Configuration=Debug --no-build --no-restore `
    --filter "FullyQualifiedName=SAAIA.Client.ToolAgent.Tests.LiveSourceBackedCandidateRecoveryMicroprobeTests.Live_qwen3_uses_role_and_anchor_memory_to_avoid_invalid_navigation_when_enabled"
Remove-Item Env:SAAIA_LIVE_SOURCE_BACKED_CANDIDATE_RECOVERY_PROBE
```

Le dernier artefact de ce probe est
`artifacts/live-source-backed-candidate-recovery-20260807-145203/report.txt` :
Qwen3 a choisi `continue_document_pagination` trois fois sur trois. Cela prouve
la stabilité locale de cette microdécision, pas la convergence du pipeline
complet.

### 7.7 Full live headless du planning de repas

Le wrapper recommandé protège contre un `--no-build` trompeur : il vérifie les
timestamps et compare le hash de l'assembly WinUI copié avec celui réellement
compilé.

```powershell
$results = Join-Path $PWD ("artifacts\client-live-final-weekly-meal-plan-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))

.\tools\run-live-model-e2e.ps1 `
    -LlmBaseUrl 'http://127.0.0.1:1234/v1' `
    -LlmModel 'Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf' `
    -TestProject '.\client\SAAIA.Client.ToolAgent.Tests\SAAIA.Client.ToolAgent.Tests.csproj' `
    -ResultsDirectory $results `
    -TestFilter 'FullyQualifiedName=SAAIA.Client.ToolAgent.Tests.LiveCuisineAgentValidationTests.Live_final_weekly_meal_plan_question_runs_through_real_client_agent_when_enabled' `
    -TimeoutMinutes 3 `
    -MaximumWorkingEvidenceItems 80 `
    -MaximumSemanticCandidatesPerAuditTurn 40 `
    -MaximumSemanticCandidateAuditConcurrency 2 `
    -StructuredSampling deterministic `
    -SkipBuild
```

Ne relever le timeout au-delà de trois minutes que pour profiler une étape
identifiée, jamais pour déclarer acceptable un pipeline qui boucle.

Le wrapper écrit :

- un TRX ;
- `sampling-profile.json` avec modèle, sampling, flags et preuve du build ;
- les artefacts produits par le test, notamment `answers-readable.txt` et
  `progress.log`.

Suivre le live sans ouvrir de fenêtre :

```powershell
Get-Content -LiteralPath (Join-Path $results 'progress.log') -Encoding UTF8 -Wait
```

Le `progress.log` du test est généralement dans le sous-répertoire créé par
`CreateReadableArtifactPath`, pas forcément à côté du TRX. Le repérer avec :

```powershell
Get-ChildItem .\artifacts -Recurse -Filter progress.log |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 5 FullName, LastWriteTime
```

### 7.8 Encodage : ne pas diagnostiquer un faux mojibake

Windows PowerShell 5 peut interpréter un fichier UTF-8 sans BOM avec un ancien
encodage. `Get-Content` sans `-Encoding UTF8` a affiché temporairement
`petit-dÃ©jeuner`, alors que les octets du fichier contiennent bien `é` U+00E9.

Toujours utiliser :

```powershell
Get-Content -LiteralPath $path -Encoding UTF8
[IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
```

Ne pas « corriger » un fichier avant d'avoir vérifié ses caractères Unicode
réels. Plusieurs regex du dépôt contiennent volontairement des variantes de
mojibake pour assainir d'anciennes données ; une recherche globale sur `Ã` ne
prouve donc pas à elle seule une corruption du source.

### 7.9 Ce que les tests automatiques ne prouvent pas

Même un test qui passe peut laisser :

- une réponse vide si l'assertion ne vérifie pas le contenu ;
- une carte source pointant le bon document mais la mauvaise page ;
- un titre de recette correct soutenu par un passage qui parle d'autre chose ;
- vingt cellules mécaniquement présentes mais sémantiquement absurdes ;
- une duplication visible sous des EvidenceIds différents ;
- une réponse acceptable obtenue une seule fois par hasard.

Le test Q019 historique est un exemple important : un live peut être vert avec
une réponse vide. Lire le JSONL/artefact directement.

### 7.10 Validation humaine obligatoire, mais tardive

Après trois runs headless consécutifs réussis sur le même binaire, faire un
seul parcours WinUI réel et vérifier :

1. compréhension de la question et éventuelle clarification naturelle ;
2. délai perçu et messages de progression ;
3. tableau 5 jours × 4 créneaux, exactement 20 cellules ;
4. vingt propositions nommées et réellement adaptées au créneau ;
5. aucune recette réutilisée ;
6. aucune rubrique, ingrédient isolé, instruction OCR ou placeholder ;
7. chaque EvidenceId visible dans la réponse correspond à une seule carte ;
8. chaque carte affiche titre, fichier et page ;
9. le clic ouvre réellement le bon fichier et la bonne page ;
10. le passage de la page soutient la proposition ;
11. une seconde demande « donne-moi un autre planning » ne réutilise aucun
    élément du premier ;
12. une question simple ne déclenche pas le pipeline lourd ;
13. une demande hors corpus est refusée honnêtement ;
14. un document nommé est retrouvé sans perdre son nom dans la navigation ;
15. accents, emojis éventuels et caractères Telegram/WinUI sont propres.

Conserver une feuille d'évaluation humaine avec notes et capture des sources.
Ne pas remplacer ce jugement par un LLM-as-judge unique.

## 8. Architecture actuelle et état réel

### 8.1 Chemin conversationnel moderne

Chemin simplifié :

```text
RagChatAgent
  -> ToolAgentOrchestrator.RunAsync
  -> routeur natif LLM
  -> SourceBackedAgentV2Runner
       -> plan / forme / rôles
       -> définition du candidat ou stratégie
       -> choix d'outils par le LLM
       -> navigation / cartes / contexte / rag.search
       -> EvidenceBundle
       -> audit sémantique groupé
       -> compatibilité / affectation
       -> writer
       -> vérificateur mécanique des sources
  -> sourcesPayload
  -> cartes WinUI
```

Le fil rouge attendu est un `EvidenceBundle` canonique unique entre retrieval,
juge, writer, vérificateur et UI. La migration vers ce contrat est avancée mais
les anciens chemins et structures ne sont pas encore tous supprimés.

### 8.2 Modularisation mesurée

Le nouveau sous-répertoire :

`client/SAAIA.Client.WinUI/ToolAgent/SourceBackedRag/`

contient 134 fichiers C#, environ 29 080 lignes. Un seul fichier atteint 2 000
lignes : `SourceBackedAgentV2Runner.cs`. La séparation par responsabilité est
réelle, mais 134 fragments et plusieurs prompts de 400 à 600 lignes créent une
autre forme de complexité. Il faut maintenant consolider par rôles cohérents,
supprimer les variantes mortes et documenter les frontières, pas simplement
continuer à ajouter un fichier par incident.

Le dossier `ToolAgent` complet contient environ :

- 377 fichiers C# ;
- 107 513 lignes ;
- 243 fichiers et 78 433 lignes directement à sa racine.

Le backend `Endpoints` contient environ 50 524 lignes, dont :

- `RagEndpoints.cs` : 36 534 lignes ;
- `DocumentsEndpoints.cs` : 2 610 lignes ;
- `SummaryEndpoints.cs` : 1 829 lignes ;
- `AdminRuntimeEndpoints.cs` : 1 360 lignes.

La refonte ne doit pas recommencer tout le produit depuis zéro. Elle doit
conserver les contrats déjà validés et extraire progressivement les primitives
de `RagEndpoints.cs` : recherche dense, sparse, fusion, rerank, navigation,
résolution canonique, projection DTO et observabilité.

### 8.3 Configuration surprenante à connaître

`SourceBackedAgentV2Options` a des valeurs par défaut de record différentes de
celles effectivement retournées par `ResolveFromEnvironment` :

- stratégie candidat : valeur du record `true`, valeur runtime par défaut
  `false` ;
- définition candidat : valeur du record `false`, valeur runtime par défaut
  `true` ;
- résolution de libellé : valeur du record `false`, valeur runtime par défaut
  `true`.

La trace du dernier live confirme :

```text
candidate_definition_enabled=true
candidate_strategy_enabled=false
candidate_audit_batch_size=6
context_tokens=4096
```

Les tests qui construisent directement le record et le client réel qui appelle
`ResolveFromEnvironment` peuvent donc exercer des architectures différentes.
Unifier les défauts ou rendre chaque test explicite avant d'interpréter un
résultat.

### 8.4 Cause précise du dernier live

Artefacts principaux :

- `artifacts/client-live-final-weekly-meal-plan-20260807-130111/progress.log` ;
- `artifacts/client-live-final-weekly-meal-plan-20260807-role-memory-anchor-audit/`
  pour le TRX.

Chronologie utile :

- routeur natif : environ 17 s ;
- reconnaissance correcte d'une grille 5 × 4 et de 20 preuves attendues ;
- première phase SourceBacked vers 74 s ;
- rôle `Déjeuner` mal décrit comme « repas principal de l'après-midi » ;
- type candidat choisi : `Produit alimentaire` ;
- hypothèse : `Pain au chocolat au lait` ;
- navigation et inventaire massifs ;
- 100 candidats audités, 24 acceptés, 76 rejetés ;
- recherche répétée sur l'hypothèse inventée ;
- deux appels identiques ensuite refusés mécaniquement comme doublons ;
- pagination relancée, 20 nouvelles preuves en toute fin de budget ;
- annulation globale à 12:00 pendant
  `CompleteCandidateColumnCompatibilityAsync` ;
- aucun writer, aucune réponse finale, aucun payload source.

Conclusion : augmenter le timeout ne répare ni la mauvaise abstraction de
candidat, ni la fuite de l'hypothèse, ni le coût d'auditer 100 à 120 candidats
avant rédaction.

### 8.5 Direction immédiate recommandée

Avant tout nouveau full live :

1. créer un microbenchmark qui rejoue exactement la demande de planning et
   capture dans un seul artefact : forme, besoin de clarification, type
   candidat, exemple éphémère, première action et durée de chaque appel ;
2. prouver que l'exemple éphémère ne peut jamais devenir une query ou un
   candidat sans avoir été observé dans une source ;
3. comparer l'actuelle définition v7 à un contrat d'intake plus court qui
   demande au LLM en une décision cohérente : compréhension, ambiguïté, forme,
   unité documentaire et première observation ;
4. conserver la liberté d'outils, mais éviter cinq appels LLM successifs avant
   la première observation documentaire ;
5. mesurer l'intérêt de faire une première collecte large de cartes, un audit
   groupé court, puis une rédaction dès que l'appariement de 20 éléments est
   possible ;
6. n'ajouter une nouvelle recherche que sur une insuffisance explicitement
   décidée par le LLM ;
7. relancer le full live seulement si les microprobes sont stables sur plusieurs
   permutations.

L'objectif n'est pas un nombre d'appels codé. Le comportement attendu est
adaptatif : une question simple peut faire routeur → retrieval → writer ; une
demande complexe ajoute uniquement les jugements et explorations dont elle a
réellement besoin.

## 9. Ingestion, indexation et provenance

### 9.1 Corpus Cuisine canonique

Les dix PDF canoniques sous `/opt/saaia/documents/Cuisine` ont été réingérés le
2026-07-28 avec `deterministic_canonical_v3` :

- 3 882 ancres ;
- 3 882 chunks SQL ;
- 3 882 points Qdrant ;
- 1 123 cartes canoniques.

Les dix copies physiques sous `Cuisine/PDF` avaient été comparées par taille et
SHA-256 puis supprimées individuellement. Le dossier dupliqué n'existe plus.

Il ne faut pas refaire cette opération sans preuve de dérive. Il faut d'abord
réinterroger le serveur et comparer révision active, chunks, cartes et points.

### 9.2 Dette de tombstones

Les anciennes lignes `Cuisine/PDF/...` restent comme tombstones `deleted` avec
leurs artefacts historiques. Six messages de chat persistés référencent encore
ces anciens chemins dans `sources_json`.

Avant purge :

1. migration générique par hash de source vers le document canonique actif ;
2. mise à jour `docPath`, `docId` et identité de révision ;
3. test réel d'ouverture des six cartes ;
4. backup ;
5. purge transactionnelle des tombstones et artefacts devenus inutiles.

Ne pas coder les dix noms Cuisine. Ne pas supprimer les tombstones avant la
migration des six cartes.

### 9.3 Ce qui reste à éprouver après Cuisine

Quand le chemin est fiable sur Cuisine, tester par ordre :

1. PDF texte simple ;
2. PDF entièrement scanné ;
3. texte natif + images contenant du texte ;
4. multi-colonnes ;
5. listes imbriquées ;
6. tableaux sur plusieurs pages avec en-têtes répétés ;
7. documents longs avec sommaire ;
8. mélange de langues ;
9. fichiers DOCX, PPTX, XLSX et images ;
10. plus tard, schémas techniques avec enrichissement multimodal/LLM serveur.

Pour chaque document, valider non seulement le nombre de chunks mais la
structure, l'ordre de lecture, les pages, titres, tableaux, légendes et liens
entre image et texte.

## 10. Sources mécaniques et cartes UI

Le problème historique « le LLM trouve une bonne recette mais ne peut pas
épingler fichier/page » vient de la différence entre pertinence sémantique et
provenance mécanique. Un texte peut être pertinent, mais si sa projection perd
`docId`, `revisionId`, `page`, `anchorId`, `chunkId` ou `contentCardId`, le
client ne peut plus construire un lien fiable après coup.

Règle : la provenance doit voyager avec le contenu dès le retrieval, dans le
`EvidenceBundle`, jusqu'au writer et au `sourcesPayload`. Le LLM choisit
l'EvidenceId ; le code résout mécaniquement cet ID vers l'unique carte
canonique. Ne jamais demander au writer de reconstruire un chemin ou une page
à partir du texte.

Le test final du planning vérifie déjà plusieurs invariants forts :

- 20 EvidenceIds dans le tableau ;
- identité distincte ;
- un EvidenceId de réponse résout exactement une carte UI ;
- présence d'un payload source.

Il reste indispensable de contrôler humainement que le passage visible de la
page soutient réellement la cellule.

## 11. Mémoire et projets

### 11.1 Modèle cible à six mémoires

- M0 — mémoire de politique ;
- M1-lite — mémoire canonique du workspace ;
- M2 — mémoire de projet ;
- M3 — mémoire de session ;
- M5 — mémoire de corpus ;
- M6 — mémoire d'exécution/observabilité.

L'historique complet des messages est une archive, pas une septième mémoire à
injecter dans chaque prompt.

### 11.2 État actuel honnête

`ToolMemory.cs`, `SourceBackedConversationMemory` et plusieurs traces existent.
Le profil historique mentionne `cdc-v3-m1lite-m3-m6`. L'ADR M2/ContextEnvelope
est défini, mais la notion complète de projet, la projection typée, les outils
de lecture à la demande et le glisser-déposer d'une discussion vers un projet
ne sont pas terminés.

Ne pas annoncer « les six mémoires fonctionnent ». Elles doivent encore être
auditées de bout en bout : propriétaire, source de vérité, lecture, écriture,
durée de vie, compaction, provenance, budget et isolation projet/tenant.

### 11.3 Comportement attendu

Après un premier planning, « donne-moi un autre planning » doit :

- comprendre le suivi sans réinjecter tout l'historique ;
- connaître les vingt éléments déjà utilisés ;
- conserver les requêtes exécutées ;
- conserver les sources observées, acceptées et rejetées avec motif ;
- éviter les recherches et sources déjà épuisées ;
- chercher de nouveaux éléments ;
- ne citer que des preuves fraîches ou explicitement réutilisées ;
- ne jamais traiter la mémoire elle-même comme preuve documentaire.

L'identité à persister doit survivre à une réingestion : hash source + document
canonique + ancre/carte stable si disponible, pas seulement un EvidenceId
éphémère `E17`.

### 11.4 Prévenir la saturation du contexte

Ne jamais injecter l'historique complet ni la mémoire projet complète. Le LLM
reçoit un `ContextEnvelope` compact et typé : contraintes actives, décisions,
éléments utilisés, lacunes, pointeurs. Il dispose d'outils pour lire plus de
mémoire à la demande.

Le budget doit être calculé avec le tokenizer réel, réserver séparément
politique, outils, mémoire, preuves et sortie, et journaliser toute compaction.
La compaction ne peut supprimer contraintes actives, identités utilisées ni
provenance.

## 12. Recherche externe et alternatives à tester

Les recherches du 2026-08-26 confirment qu'il existe plusieurs options
intéressantes. Elles doivent devenir des **expériences A/B**, pas de nouveaux
frameworks ajoutés sans mesure.

### 12.1 Tool calling Qwen3

La documentation Qwen recommande le format Hermes/nous pour maximiser le tool
calling et déconseille les templates ReAct basés sur des stop words pour les
modèles raisonneurs. Qwen-Agent est l'implémentation canonique de référence :

- [Qwen3 Function Calling](https://github.com/QwenLM/Qwen3/blob/main/docs/source/framework/function_call.md)
- [Qwen-Agent](https://github.com/QwenLM/Qwen-Agent)

Expérience prioritaire : faire exécuter les mêmes dix contrats courts par le
client actuel et par un oracle minimal utilisant le template officiel
nous/Hermes. Comparer conformité, tool-call exact, retries, tokens, TTFT et
durée. Ne pas migrer tout le client .NET vers Python ; Qwen-Agent sert d'oracle
et de référence de format.

### 12.2 llama.cpp

La documentation officielle confirme : `--jinja` est requis pour le function
calling OpenAI, le format natif/générique doit être visible dans les logs et
`/props` permet d'inspecter le template. Elle avertit aussi que des
quantifications KV extrêmes, par exemple q4, peuvent dégrader fortement le tool
calling :

- [llama.cpp Function Calling](https://github.com/ggml-org/llama.cpp/blob/master/docs/function-calling.md)
- [Qwen3 avec llama.cpp](https://github.com/QwenLM/Qwen3/blob/main/docs/source/run_locally/llama.cpp.md)

Le profil actuel garde les caches K/V en f16, ce qui est prudent pour la
qualité. Toute réduction doit être testée sur les contrats structurés, pas
seulement sur la VRAM.

Les builds plus récents peuvent corriger ou introduire des problèmes de parser.
Épingler le build dans chaque artefact et rejouer la suite de qualification
avant promotion.

### 12.3 Qwen3.5 et modèles futurs

Qwen-Agent annonce des générations plus récentes, mais des issues llama.cpp de
2026 documentent encore des tool calls placés dans `reasoning_content`, des
parsers cassés selon le template et des échecs avec longs contextes/outils
optionnels :

- [issue llama.cpp 21158](https://github.com/ggml-org/llama.cpp/issues/21158)
- [issue llama.cpp 20164](https://github.com/ggml-org/llama.cpp/issues/20164)
- [issue llama.cpp 26530](https://github.com/ggml-org/llama.cpp/issues/26530)

Ne pas remplacer Qwen3-4B simplement parce qu'un numéro de génération est plus
récent. Dès qu'un petit Qwen3.5/3.6 officiel et une quantification adaptée sont
disponibles, exécuter le protocole modèle complet : qualité autonome, tool
calling, JSON schema, contexte long, mémoire, plan de repas, sources et latence
sur P520.

### 12.4 Docling et chunking structurel

Docling fournit un `HybridChunker` token-aware qui commence par la hiérarchie
documentaire, sépare seulement les éléments trop longs, fusionne les petits
peers partageant titres/légendes et peut répéter les en-têtes de tableaux :

- [Docling — Chunking](https://github.com/docling-project/docling/blob/main/docs/concepts/chunking.md)

Comparer sur un sous-corpus :

- projecteur canonique SAAIA actuel ;
- `HierarchicalChunker` ;
- `HybridChunker` avec tokenizer `multilingual-e5-base` ;
- `LineBasedTokenChunker` pour tableaux/listes.

Mesurer exactitude page/structure, rappel retrieval, taille des chunks, doublons
et temps d'ingestion. Ne pas remplacer les ancres canoniques qui fonctionnent
avant d'avoir un résultat supérieur.

### 12.5 Hybrid retrieval dans Qdrant

La Query API Qdrant peut exécuter dense et sparse en `prefetch`, puis fusionner
par RRF/DBSF ou en plusieurs étapes, côté moteur :

- [Qdrant — stratégie hybrid search](https://github.com/qdrant/skills/blob/main/skills/qdrant-search-quality/search-strategies/hybrid-search/SKILL.md)
- [Qdrant Alloy](https://github.com/qdrant-labs/qdrant-alloy)

Une expérimentation pourrait déplacer une partie de la fusion mécanique du
monolithe `RagEndpoints.cs` vers une requête Qdrant déclarative. Comparer sur
les mêmes candidats et conserver tous les scores, retrievers et identités dans
la trace. Le LLM reste juge de pertinence finale.

### 12.6 Navigation hiérarchique / PageIndex

PageIndex explore un document comme un sommaire : structure hiérarchique, pages
et lecture à la demande, sans dépendre d'un vector store pour le chemin
intra-document :

- [VectifyAI PageIndex](https://github.com/VectifyAI/PageIndex)
- [démo agentic vectorless RAG](https://github.com/VectifyAI/PageIndex/blob/main/examples/agentic_vectorless_rag_demo.py)

Cette approche correspond à l'intuition utilisateur sur la navigation par
sommaire. Elle est surtout intéressante pour un document nommé ou un corpus
long et structuré. Ne pas remplacer le retrieval hybride global : tester un
retriever expérimental `tree_navigation` en parallèle, sur Q019 et quelques
documents longs, avec coût d'indexation et appels LLM inclus.

### 12.7 Contextual Retrieval

Le principe consiste à préfixer chaque chunk avec un contexte court le situant
dans le document avant embeddings et BM25 :

- [Anthropic — Contextual Retrieval](https://www.anthropic.com/engineering/contextual-retrieval)

SAAIA dispose déjà de titres, sections, légendes, pages et ancres. Tester
d'abord une contextualisation déterministe issue de ces métadonnées. Une
contextualisation produite par le LLM serveur peut être une variante future,
mais elle doit être versionnée, non considérée comme preuve et comparée à la
source brute.

### 12.8 Reranking

Le serveur utilise déjà un reranker multilingue GTE. BGE propose des rerankers
multilingues et BGE-M3 couvre dense, lexical et multi-vector :

- [FlagEmbedding — rerankers](https://github.com/FlagOpen/FlagEmbedding/blob/master/examples/inference/reranker/README.md)
- [FlagEmbedding](https://github.com/FlagOpen/FlagEmbedding)

Ne changer que sur un benchmark annoté français/multilingue : nDCG/Recall@K,
latence p50/p95, RAM/VRAM et impact final sur les sources utiles. La présence
d'un modèle « plus récent » ne suffit pas.

### 12.9 Évaluation RAG

DeepEval et RAGAS fournissent des idées pour séparer retrieval et génération :

- [DeepEval](https://github.com/confident-ai/deepeval)
- [guide d'évaluation RAG DeepEval](https://github.com/confident-ai/deepeval/blob/main/docs/guides/guides-rag-evaluation.mdx)
- [RAGAS — génération de testset](https://github.com/vibrantlabsai/ragas/blob/main/docs/getstarted/rag_testset_generation.md)

Utiliser ces frameworks hors du chemin de production, comme harnais. Construire
un corpus de goldens validés humainement et mesurer séparément : rappel des
preuves, précision/rang, foi de la réponse, exactitude des citations, latence,
tokens, répétabilité et satisfaction humaine.

### 12.10 GraphRAG

Microsoft indique que GraphRAG est coûteux à indexer et désormais surtout en
maintenance/recherche :

- [Microsoft GraphRAG](https://github.com/microsoft/graphrag)

Ce n'est pas la priorité pour les recettes, documents nommés et PDF techniques
actuels. Il peut être utile plus tard pour des questions de synthèse globale
sur entités/relations, mais ne doit pas devenir une dépendance avant un besoin
mesuré.

### 12.11 Discipline de recherche forums/GitHub

À chaque blocage récurrent :

1. formuler une reproduction minimale ;
2. chercher documentation officielle, issues ouvertes/fermées et changelog ;
3. noter versions exactes modèle/runtime/hardware ;
4. distinguer témoignage, hypothèse et correctif fusionné ;
5. reproduire localement ;
6. comparer à la baseline ;
7. conserver le lien et le résultat dans l'artefact d'expérience.

Les forums servent à découvrir des pistes. Ils ne remplacent ni une source
primaire ni un benchmark SAAIA.

## 13. Erreurs à ne plus commettre

### Architecture et qualité

- Ne pas ajouter une heuristique locale à chaque échec live.
- Ne pas confondre réparation JSON et jugement sémantique.
- Ne pas imposer un outil ou une quantité fixe d'appels au LLM.
- Ne pas transformer silencieusement la requête choisie par le LLM.
- Ne pas multiplier toutes les cartes enfants d'un hit parent comme preuves.
- Ne pas laisser un exemple hypothétique entrer dans la recherche réelle.
- Ne pas faire de la mémoire une preuve.
- Ne pas compenser une mauvaise ingestion par du nettoyage sémantique client.
- Ne pas considérer une catégorie lexicale codée comme décision du LLM.
- Ne pas confondre « résultat de recherche pertinent » et « source cliquable
  mécaniquement résolue ».

### Performance

- Ne pas augmenter seulement les timeouts.
- Ne pas lancer immédiatement un full live de 12 à 30 minutes.
- Ne pas faire trois appels séquentiels par candidat si un audit groupé suffit.
- Ne pas remplir le contexte 4K avec historique, prompts répétés et 100 preuves.
- Ne pas quantifier K/V agressivement sans test de tool calling.
- Ne pas comparer les modèles uniquement dans l'ancien pipeline Qwen2.5.
- Ne pas présenter tok/s comme qualité globale.

### Tests

- Ne pas utiliser `--no-build` sans preuve que les assemblies correspondent
  aux sources.
- Ne pas mélanger x64 et AnyCPU.
- Ne pas déclarer un live vert sans lire réponse, sources et TRX.
- Ne pas considérer 1 235 tests déterministes comme preuve que le planning est
  bon.
- Ne pas lancer WinUI à chaque itération ; utiliser les tests headless.
- Ne pas supprimer la validation humaine finale.
- Ne pas accepter un succès obtenu une seule fois ; répéter et permuter.

### Serveur et données

- Ne pas exposer `.env.server-linux`.
- Ne pas lancer `restore.ps1` sur la production pour tester.
- Ne pas utiliser `-WithDependencies` sans nécessité.
- Ne pas purger les tombstones avant migration des six cartes historiques.
- Ne pas supprimer récursivement des documents sans realpath, hash et inventaire.
- Ne pas conclure que le serveur exécute le commit seul ; vérifier le source
  hash.
- Ne pas déployer sans `/ready` et artefact de validation.

### Dépôt

- Ne pas faire `git add -A` sur 394 fichiers non suivis sans revue par lot.
- Ne pas faire de reset/clean destructeur.
- Ne pas commiter modèles, artefacts live, secrets ou gros rapports générés.
- Ne pas garder indéfiniment le pipeline historique après équivalence prouvée.
- Ne pas supprimer le pipeline historique avant tests de caractérisation.
- Ne pas poursuivre des jours la même stratégie : après deux ou trois échecs de
  même classe, faire un point d'architecture et un benchmark alternatif.

### Encodage et Telegram

- Ne pas passer un long texte accentué directement dans une ligne de commande
  Windows PowerShell si un fichier UTF-8 peut être utilisé.
- Ne pas lire un fichier UTF-8 sans `-Encoding UTF8` sous Windows PowerShell 5.
- Ne pas envoyer secrets, stack traces brutes ou extraits de documents sur
  Telegram.

## 14. Plan de travail priorisé pour la nouvelle discussion

### Phase A — Reprendre proprement et figer la baseline

1. lire ce rapport et les deux ADR ;
2. vérifier Git, `/ready`, port LLM et `/props` ;
3. créer un répertoire d'artefact daté ;
4. compiler x64 Debug une fois ;
5. rejouer les tests ciblés ;
6. rejouer les probes backend ;
7. ne modifier le code qu'après cette baseline.

### Phase B — Réduire l'orchestration avant retrieval

1. instrumenter chaque appel LLM pré-retrieval ;
2. isoler définition v7, rôles de colonnes et première action ;
3. empêcher mécaniquement la fuite des hypothèses éphémères ;
4. tester un intake structuré plus court et cohérent ;
5. comparer format actuel, Hermes/nous officiel et éventuel oracle Qwen-Agent ;
6. retenir la variante par qualité + latence, pas préférence.

Critère : bonne unité documentaire, bon périmètre et première observation en
moins de 30 s sur au moins cinq permutations.

### Phase C — Collecte et audit adaptatifs

1. commencer par une observation à fort rendement choisie par Qwen3 ;
2. conserver pagination, rendement, acceptations et refus dans un état compact ;
3. audit groupé des candidats réellement nouveaux ;
4. calcul mécanique du matching distinct maximal, sans juger la compatibilité ;
5. demander une nouvelle recherche uniquement si le juge LLM identifie une
   lacune ;
6. arrêter la collecte dès que le writer a assez de matière.

Critère : aucune révision répétée de 100 candidats ; première preuve utile
rapidement ; pas de boucle sur une query dupliquée.

### Phase D — Writer, EvidenceBundle et cartes

1. writer à partir des candidats approuvés et identités canoniques ;
2. vingt affectations distinctes pour le planning ;
3. vérification mécanique EvidenceId → carte unique ;
4. réparation focalisée seulement sur les cellules invalides ;
5. aucune reconstruction de chemin/page par le LLM.

Critère : réponse professionnelle et 20 cartes correctes en trois runs
headless consécutifs.

### Phase E — Questions simples et généricité

Valider au minimum :

- dessert simple ;
- question factuelle exacte ;
- navigation par document nommé ;
- suivi conversationnel ;
- clarification réellement ambiguë ;
- refus hors corpus ;
- question dans une autre catégorie ;
- langues prises en charge.

Critère : aucune question simple ne prend le chemin planning lourd.

### Phase F — Mémoire et projets

1. inventaire des six mémoires réelles ;
2. implémentation M2 projet + ContextEnvelope ;
3. outils de lecture mémoire à la demande ;
4. suivi requêtes/sources/candidats/éléments utilisés ;
5. test « autre planning sans doublons » ;
6. modèle de projet multi-discussions ;
7. rattachement/glisser-déposer d'une discussion vers un projet ;
8. isolation, compaction et budgets.

### Phase G — Simplification et nettoyage

1. cartographier chemins atteignables ;
2. tests de caractérisation ;
3. supprimer pipeline ancien, flags et heuristiques devenus morts ;
4. extraire `RagEndpoints.cs` par primitives ;
5. consolider les 134 fichiers SourceBacked en modules cohérents ;
6. découper les tests de 10 554 lignes ;
7. migrer/purger les tombstones ;
8. revue de chaque groupe avant staging ;
9. commits petits, cohérents et réversibles.

### Phase H — Validation finale

1. tests backend ;
2. tests client déterministes ;
3. question bank ;
4. répétitions live ;
5. tests autres catégories/documents ;
6. parcours humain WinUI ;
7. sources cliquables ;
8. rapport qualité/latence ;
9. état Git propre ;
10. commits et comparaison avec origin.

## 15. Protocole de prise de recul

Déclencher obligatoirement une revue de stratégie lorsque :

- deux full lives échouent au même endroit ;
- une correction ajoute une nouvelle heuristique métier ;
- le temps s'améliore mais la qualité baisse ;
- le contexte reste à plus de 90 % pendant plusieurs tours ;
- plus de la moitié des candidats sont rejetés après matérialisation ;
- une réparation de protocole se répète ;
- une même requête est appelée deux fois ;
- le writer n'est pas atteint en 120 s ;
- un test vert contredit l'observation humaine.

La revue doit répondre par écrit :

1. quelle hypothèse vient d'être invalidée ;
2. où est la première divergence dans la trace ;
3. quelles décisions appartiennent au LLM ou au code ;
4. quelles alternatives externes existent ;
5. quelle expérience minimale les départage ;
6. quels critères entraînent conservation, rollback ou pivot.

Ne pas continuer à empiler des patchs pendant cette revue.

## 16. Rapports Telegram

Outil :

`C:\Users\MBirchler\Desktop\ecom\SAAIA\SAAIA - Notifier\notify-codex.ps1`

Les variables `TELEGRAM_BOT_TOKEN` et `TELEGRAM_CHAT_ID` sont lues depuis
l'environnement. Ne jamais les afficher.

Pour préserver les accents, écrire d'abord un petit rapport UTF-8 puis utiliser
`-MessageFile` :

```powershell
& 'C:\Users\MBirchler\Desktop\ecom\SAAIA\SAAIA - Notifier\notify-codex.ps1' `
    -MessageFile $reportPath `
    -RequireMessage
```

Le script lit avec `-Encoding UTF8`, encode le JSON en UTF-8 sans BOM, vérifie
le texte retourné par Telegram et découpe les longs messages sans casser les
paires surrogate.

Cadence demandée : environ une fois par heure, ou à un jalon réellement utile.
Un bon rapport contient : objectif, actions, preuves, mesures, résultat,
problème restant et prochaine décision. Ne pas envoyer un message pour chaque
petit test.

## 17. Définition de « terminé »

Le chantier n'est terminé que si :

- le LLM client reste l'orchestrateur sémantique ;
- la demande complexe est comprise ou clarifiée intelligemment ;
- le planning 5 × 4 est complet, professionnel et sans doublons ;
- chaque élément est prouvé et ouvre le bon fichier/page ;
- une seconde demande produit vingt nouveaux éléments ;
- les questions simples sont rapides ;
- les documents nommés et suivis mémoire fonctionnent ;
- l'insuffisance est honnête ;
- les dix documents Cuisine restent canoniques ;
- les autres catégories et formats passent ;
- la mémoire projet et le contexte long sont bornés ;
- les traces expliquent chaque étape ;
- les tests déterministes, lives répétés et validation humaine concordent ;
- le code mort et les anciens modèles/pipelines sont supprimés ;
- `RagEndpoints.cs` et les gros tests sont modularisés ;
- le dépôt est propre et les commits cohérents ;
- aucun secret n'a été exposé.

## 18. Prompt de démarrage à copier dans la nouvelle discussion

> Travaille dans
> `C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG`. Lis entièrement
> `REPRISE-2026-08-26-NOUVELLE-DISCUSSION-RAG-ULTRA-COMPLETE.md`, puis
> `GOAL-2026-07-28-RAG-CANONIQUE-LLM-FIRST.md` et
> `ADR-2026-07-08-rag-llm-orchestration-source-backed.md`. Vérifie par toi-même
> l'état Git, le `/ready` distant, le runtime Qwen3 local et les derniers
> artefacts avant toute modification. Ne révèle jamais les secrets : charge-les
> depuis `infra/.env.server-linux` ou le store DPAPI. Utilise les tests headless
> du projet ToolAgent, les probes backend puis les microprobes LLM ; ne lance
> WinUI qu'après les validations automatiques pour la validation humaine finale.
> Le LLM client doit rester le décideur sémantique ; le code ne fait que les
> opérations mécaniques, budgets, contrats, provenance et sécurité. Commence
> par corriger de manière générique la mauvaise définition d'objet candidat et
> la fuite de l'hypothèse éphémère vers la recherche, puis réduis les appels
> avant retrieval en comparant objectivement les variantes. Si la même classe
> d'échec se répète, prends du recul, recherche documentation, GitHub et projets
> similaires, construis une expérience discriminante et change de stratégie si
> les mesures le justifient. Préserve le worktree sale, n'effectue aucun reset
> destructeur et documente chaque jalon important en français clair sur
> Telegram environ une fois par heure.

## 19. Première séquence concrète de la prochaine discussion

```text
1. Revalidation Git + ready + SSH + ports LLM.
2. Démarrage llama-server headless et capture /props.
3. Build x64 Debug unique.
4. Tests déterministes ciblés.
5. Probes backend cartes/navigation/retrieval.
6. Microprobe exact : demande repas -> type candidat -> première action.
7. Test d'invariant : une hypothèse non observée ne devient jamais query.
8. A/B contrat v7 vs intake compact vs template Qwen officiel.
9. Choix factuel sur qualité, latence, tokens et stabilité.
10. Seulement ensuite, full live plafonné à trois minutes.
11. Analyse réponse + EvidenceBundle + source cards.
12. Répétition, autres questions, mémoire, puis WinUI humain.
13. Nettoyage et commits par lots après équivalence prouvée.
```

Ce rapport doit être mis à jour si l'un des faits suivants change : commit,
révision serveur, modèle/runtime, corpus canonique, blocage live, architecture
mémoire, commandes de test, emplacements de secrets ou procédure de
déploiement.
