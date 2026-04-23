# SAAIA - TODO Produit Client + Backend

> Derniere mise a jour : 2026-04-23
> Base CDC : v3.1 (remplace v3.0 — document unique de reference)
> Branche : SAAIA_V3.0
> Reference locale : `documents/cdc/CDC Agent AI - RAG - V3.1.md`
> Note : `documents/` est ignore par Git dans ce repo. Le TODO ci-dessous est donc la source suivie dans le worktree partage.
> Scope : client WinUI (LLM local, tuning, governance) + backend RAG (.NET 8, Postgres, Qdrant, endpoints)

## Legende

- `[x]` = fait et verifie
- `[ ]` = a faire
- `[~]` = partiellement fait ou a surveiller
- `[N/A]` = non applicable a ce stade

---

## Etat global

| Bloc | Scope | Etat | Notes |
|---|---|---|---|
| Architecture backend | Backend | [x] Solide | Gouvernance runtime refactoree, service monolithique decompose |
| Catalogue / endpoints documents | Backend | [x] Solide | Cache HTTP, ETag/304, surfaces principales alignees |
| Retrieval | Backend | [x] Excellent | Exact -> BM25 -> dense -> fusion -> rerank -> linked context |
| Evidence pack `/rag/search` | Backend | [x] Tres bon | `hypQuestionsMatched` et offsets derives actifs |
| Gouvernance runtime A/B | Backend | [x] Solide | Core / A / B gouvernes, warmup, hard gates, KPI ops — artefacts dynamiques exposes via ~30 endpoints admin |
| Capacite A | Backend | [x] Close v1 | LLM local, fallback, score qualite, KPI A, vue admin |
| Capacite B | Backend | [x] Close v1 | Worker LLM local, fallback, KPI B, revue qualite |
| Capacite C | Backend | [N/A] | Hors perimetre, absente par design |
| Tests backend | Backend | [x] Verts | 335 tests, 29 fichiers `*Tests.cs`, 1 fixture partagee |
| Migrations SQL | Backend | [~] Stables | 28 fichiers, doublons legacy 004/008 documentes safe |
| Tuning LLM client | Client | [x] Fait Patch 1+2 | GgufMetadataReader, ngl=block_count, batch>=512, ctx=3072, ubatch=256, threads-batch=6, flash-attn CUDA auto |
| Budget VRAM observe (DXGI) | Client | [~] Enforce v1 | `hardware_probe.json` capture RAM, GPU, fingerprint, secteur/batterie et budget DXGI ; hard gate budget DXGI branche sur `warmup_profiles.json` |
| Gouvernance llama-server client | Client | [~] Patch 5 avance | Artefacts locaux, `QualifiedProfile`, checksums, warmup gate, harnais TTFT/tok/s multi-scenarios, rollback, blacklist, hardware_probe, policy batterie, runtime compatibility policy et triggers hardware/driver/runtime/modele poses ; runtime v1, statuts UX et quarantaine checksum modele en place |
| Cycle de vie runtime (sleep/wake) | Client | [x] Runtime v1 | `EagerLoad` explicite, idle timeout pilote par profil/policy, drain via heartbeat et wake a la demande avant generation |
| Checksums modeles | Client | [x] Local pack verifie | Infrastructure SHA-256 presente ; mismatch connu -> quarantaine `.quarantine` + journal `acquisition_log.json` ; 8 modeles locaux verifies dans `model_catalog.json` |
| Endpoint support bundle admin | Backend | [x] Fait Patch 3 | `POST /admin/support/bundle` presente — ZIP stagé, artifacts/missingArtifacts, auth X-Admin-Key |

---

## Decision produit

- [x] La cible retenue est desormais `CDC v3.1 complet` (remplace v3.0)
- [x] Le scenario `v3.0-lite` est abandonne
- [x] A et B restent gouvernees avec runtime LLM serveur local + fallback explicite
- [x] vLLM reste reserve au serveur Linux GPU (Capacite B premium) - jamais runtime universel

---

## Derniere validation confirmee

- [x] `dotnet build backend/SAAIA.Backend/SAAIA.Backend.csproj` — OK, 0 Warning
- [x] `dotnet build client/SAAIA.Client.WinUI/SAAIA.Client.WinUI.csproj -p:Platform=x64 -p:Configuration=Debug` — OK, 0 Warning
- [x] `dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj -p:NuGetAudit=false -nologo -m:1` — 335/335 verts
- [x] `dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj` — 312/312 verts
- [x] `git diff --check` sans nouvelle erreur bloquante
- [x] Revalidation client 2026-04-23 apres diagnostic runtime local : 316/316 verts
- [x] Warnings CRLF restants connus sur quelques fichiers deja presents dans le repo

---

## Plan de patchs — Phase 0A (tuning LLM bench-confirmed, §18.4 CDC v3.1)

Fixes confirmes par bench machine de reference (Quadro P520, Qwen 2.5 3B Q4_K_M, build b8149).
**Ordre d'execution impose** : Patch 1 avant Patch 2 (Patch 2 depend du lecteur GGUF de Patch 1).

---

### Patch 1 — Lecteur GGUF minimal + ngl depuis metadata + ctx par defaut

**Objectif** : sortir les constantes `ngl` du code applicatif, lire le vrai `block_count` depuis le fichier GGUF.

**Fichiers touches** :
- `client/SAAIA.Client.WinUI/Services/GpuDetector.cs` (nouveau : `GgufMetadataReader` interne ou classe separee)
- `client/SAAIA.Client.WinUI/Services/AppSettings.cs` (changer default ExtraArgs)
- `client/SAAIA.Client.WinUI/MainWindow.xaml` (ligne 464 : `PlaceholderText="--ctx-size 4096"` -> `"--ctx-size 3072"`)

**Travaux** :
- [x] Implémenter `GgufMetadataReader` : lecture binaire minimale du header GGUF (magic, version, metadata key-value) sans charger les poids
  - Lire `llm.block_count` -> type uint32
  - Lire `llm.attention.head_count_kv` -> type uint32
  - Spec GGUF publique disponible sur github.com/ggerganov/ggml
- [x] `GpuDetector.ComputeAutoTuning(GpuInfo?)` : remplacer le tableau hardcode (24/32/48/72/99) par `ngl = ggufMetadata.BlockCount`
  - IMPORTANT : cibler l'overload `ComputeAutoTuning(GpuInfo?)` — c'est celui utilise par `LocalLlmBootstrapper` via `bestGpu`
  - L'overload `ComputeAutoTuning(NvidiaGpuInfo?)` peut rester en compatibilite mais doit aussi etre mis a jour
  - Convention CDC : `ngl = block_count` (blocs transformer uniquement, pas +1)
  - Pour Qwen 2.5 3B : `block_count = 36` donc `ngl = 36`
  - Fallback si lecture GGUF impossible : conserver la logique tier VRAM comme securite (logguer un warning)
- [x] `GpuDetector.ComputeAutoTuning` : garantir `batch >= 512` dans tout profil CUDA (correction tiers 192/256/384 qui violent LLM-009)
  - Profil bench Phase 0 reference : `ngl=36 / batch=1024 / ubatch=256 / ctx=3072`
- [x] `AppSettings.ExtraArgs` default : `"--ctx-size 3072"` (remplace `"--ctx-size 4096"`)
  - CDC : 3072 pour GPU < 6 Go ; 4096 seulement si valide par warmup

**Tests a lancer / a ajouter** :
- [x] Test unitaire `GgufMetadataReader` : lire le modele present sur la machine de dev, verifier `block_count == 36` et `head_count_kv == 2` pour Qwen 2.5 3B Q4_K_M
  > Squelette fourni dans CODEX-BRIEF-PHASE0.md — necessite le .gguf sur la machine de dev
- [x] Test `ComputeAutoTuning` : verifier que ngl provient du GGUF, que batch >= 512 sur GPU, que le fallback est safe si GGUF illisible
  > Squelette fourni dans CODEX-BRIEF-PHASE0.md — necessite le .gguf sur la machine de dev
- [x] `dotnet test backend/SAAIA.Backend.Tests/...` vert (333/333) — passe

**Criteres de sortie** :
- [x] ngl n'est plus une constante, il provient du GGUF
- [x] batch >= 512 pour tout profil CUDA
- [x] ctx default = 3072
- [x] tests GGUF : joues avec le .gguf reel Qwen2.5-3B-Instruct-Q4_K_M present sur la machine cible

---

### Patch 2 — ubatch + threads-batch + flash-attn conditionnel + logs runtime

**Objectif** : ajouter les trois parametres manquants dans `ApplyAutoTuningFlags` et activer les logs complets pour les diagnostics bench.

**Fichiers touches** :
- `client/SAAIA.Client.WinUI/Services/LocalLlmBootstrapper.cs`
- `client/SAAIA.Client.WinUI/Services/AppSettings.cs` (nouveaux champs structures)
- `client/SAAIA.Client.WinUI/Services/LlamaCppProcessManager.cs` (logs)

**Travaux** :
- [x] `AppSettings` : ajouter champs structures `UbatchSize` (int, default 256), `ThreadsBatch` (int, default 6), `FlashAttn` (bool?, default null = auto)
  - Ces champs remplacent progressivement la chaine libre ExtraArgs pour les parametres de tuning principaux
- [x] `LocalLlmBootstrapper.ApplyAutoTuningFlags` : injecter `--ubatch-size <UbatchSize>` si absent dans ExtraArgs
- [x] `LocalLlmBootstrapper.ApplyAutoTuningFlags` : injecter `--threads-batch <ThreadsBatch>` si absent dans ExtraArgs
- [x] `LocalLlmBootstrapper.ApplyAutoTuningFlags` : logique `flash-attn` conditionnel CUDA
  - Proposer si runtime detecte comme CUDA et build supporte (pas de seuil compute capability en dur)
  - Sur build b8149 : confirme disponible SM 6.1 (Pascal) — a noter en commentaire
- Implementation : `--flash-attn on/off` ajoute si CUDA runtime detecte (bench llama-server local : valeur obligatoire)
  - Fallback : si warmup echoue avec flash-attn, relancer sans (voir Phase 3 warmup gate)
- [x] `LlamaCppProcessManager.StartAsync` : ligne de commande complete loggee via `ClientLog.Info` avant demarrage
- [x] Nettoyage `strictMode` legacy — PERIMETRE EXACT (4 points) :
  - [x] `AppSettings.cs` : suppression lecture `ls.Values["llm.strictMode"]` (LocalSettings legacy)
  - [x] `AppSettings.cs` : suppression lecture JSON propriete `StrictMode` (file fallback legacy)
  - [x] `Provisioning.cs` : suppression propriete `LegacyStrictMode` et son mapping
  - `UserSettingsDialog.xaml.cs` : NE PAS TOUCHER — et de toute facon non compile

**Tests a lancer / a ajouter** :
- [x] Test `ApplyAutoTuningFlags` : verifier presence de `--ubatch-size`, `--threads-batch`, `--flash-attn on/off` dans les args produits pour un profil CUDA
- [x] Test regression : profil CPU ne recoit pas flash-attn ni `-ngl`
- [x] `dotnet test backend/SAAIA.Backend.Tests/...` vert (335/335) — passe
- [x] `dotnet test client/SAAIA.Client.ToolAgent.Tests/...` vert (258/258) — passe

**Criteres de sortie** :
- [x] ubatch, threads-batch, flash-attn on/off presents dans les args CUDA
- [x] AppSettings contient les champs structures (plus seulement ExtraArgs string)
- [x] Ligne de commande complete loggee au demarrage du runtime
- [x] strictMode proprement supprime

---

## Plan de patchs — Phase 0B (securisation runtime actuel + endpoint contractuel)

Travaux independants de la gouvernance complete, livrable avant Phase 3.

---

### Patch 3 — POST /admin/support/bundle + checksums minimaux modeles

**Objectif** : creer l'endpoint contractuel du bundle admin (avec ce qui existe), et ajouter les checksums sur les modeles connus.

**Fichiers touches** :
- `backend/SAAIA.Backend/Endpoints/AdminRuntimeEndpoints.cs` (nouveau endpoint)
- `client/SAAIA.Client.WinUI/Services/LocalLlmBootstrapper.cs` (Sha256Hex)
- `client/SAAIA.Client.WinUI/Services/SupportBundleBuilder.cs` (enrichissement progressif)

**Travaux** :
- [x] `AdminRuntimeEndpoints.cs` : ajouter `POST /admin/support/bundle`
  - [x] Acces `X-Admin-Key` obligatoire (AdminAuth.EnsureAdmin)
  - [x] Phase 0B : exporter artefacts governance si presents, lister les manquants dans missingArtifacts
  - [x] Response : `{ "bundlePath": "...", "artifacts": [...], "missingArtifacts": [...] }`
- [x] CHECKSUMS MODELES :
  - [x] Warning logge si `Sha256Hex = null` (ne bloque pas le telechargement en Phase 0B)
  - [x] Renseigner les SHA-256 reels des modeles locaux disponibles
    > Qwen2.5-3B-Instruct-Q4_K_M : `9c9f56a391a3abbd5b89d0245bf6106081bcc3173119d4229235dd9d23253f94`
    > Qwen2.5-3B-Instruct-Q6_K_L : `930d792ba9cebbb98faaef6755c62b47cb24bb2d16fb10a338ac80d721b81796`
    > Qwen2.5-3B-Instruct-Q8_0 : `12491ec9f03aab7f0b96cdb7742695e6583d17ee129de48332d04b9cf6acd960`
    > Mistral-7B-Instruct-v0.3-IQ3_M : `4ea14c5a6c787ac2703505f04a4ee746f746d1ace3ffd907af28f6f179e6b224`
    > Mistral-7B-Instruct-v0.3-Q4_K_M : `56d2db1ee4e4330338433c3a2d1f98f3d647db9cef785fd6e640061e1c98dde2`
    > Gemma-4-E2B-it-Q4_K_M : `ac0069ebccd39925d836f24a88c0f0c858d20578c29b21ab7cedce66ee576845`
    > Gemma-4-E2B-it-Q8_0 : `6db0088e7e2b6459dfb29fa59b0b1d7299d249ef28debc464d4d564caf444511`
    > Gemma-4-E4B-it-Q4_K_M : `dff0ffba4c90b4082d70214d53ce9504a28d4d8d998276dcb3b8881a656c742a`
    > Script local mis a jour : `tools/compute-model-reference-checksums.ps1` scanne les variantes locales + legacy
- [x] `SupportBundleBuilder` (client leger) : pas de changement en Patch 3 — Phase 3 l'enrichira quand les artefacts governance existeront

**Tests a lancer / a ajouter** :
- [x] Test contractuel `POST /admin/support/bundle` : status 200, champ `bundlePath` present, response JSON conforme
- [x] Test auth : sans contexte admin -> rejet `UnauthorizedAccessException`
- [x] `dotnet test backend/SAAIA.Backend.Tests/...` vert (335/335) — passe

**Criteres de sortie** :
- [x] `POST /admin/support/bundle` repond 200 avec bundle (meme partiel)
- [x] Test contractuel backend ajoute
- [x] Checksums : warning en place ; valeurs reelles SHA-256 renseignees pour les 8 modeles locaux
- [x] Scope dual bundle documente : bundle leger user (SupportBundleBuilder) != bundle complet admin (AdminRuntimeEndpoints)

---

## Plan de patchs — Phase 0 — Maintenance legacy (P2)

- [ ] Renommer les migrations legacy `004` / `008` dans une fenetre de maintenance
- [x] Le runner est deja verrouille pour autoriser seulement les doublons legacy connus
- [~] Surveiller la redensification de `RuntimeCoreRetrievalWarmupEvaluator` ; extraire une couche dediee si necessaire

---

## Plan de patchs — Phase 3 (gouvernance modeles LLM complete, §5.8, §9, CDC v3.1)

Ces items constituent la gouvernance LLM complete. Ils peuvent commencer en parallele des Patch 1-3 mais dependent des squelettes JSON pour le reste.

---

### Patch 4 — Squelettes JSON gouvernance + QualifiedProfile + persistance minimale

**Objectif** : poser la fondation des artefacts de gouvernance sans bloquer les autres chantiers.

**Fichiers touches** :
- Nouveau repertoire `%LOCALAPPDATA%\SAAIA\governance\` cote client
- `client/SAAIA.Client.WinUI/Services/` : nouveaux fichiers `ModelCatalogStore.cs`, `WarmupProfileStore.cs`, `GovernanceArtifactStore.cs`
- `client/SAAIA.Client.WinUI/Services/AppSettings.cs` (champ `QualifiedProfile`)

**Travaux** :
- [x] Creer squelette `model_catalog.json` (modelId, version, source, checksum, licenseFamily, commercialUseThresholdMau, etats metier/artefact)
  - Exemple Qwen : `licenseFamily = "qwen"`, `commercialUseThresholdMau = 100000000`
  - Ne jamais utiliser `"licenseType": "apache-2.0"` pour Qwen Research
- [x] Creer squelette `model_collections.json` (collections client/backend, scope, modelIds, visibleInInstaller)
- [x] Creer squelette `model_policy.json` (allowDiscovery=false, requireChecksum=true, maxActiveModelsClient=1, blacklistRef)
- [x] Creer squelette `model_sources.json` (huggingface, http-mirror, local-bundle avec requiresChecksum et allowedInAirGap)
- [x] Creer squelettes runtime : `warmup_profiles.json`, `warmup_results.json`, `hardware_probe.json`, `last_known_good_profile.json`, `blacklist.json`, `capability_state.json`, `acquisition_log.json`
- [x] Creer classe / record `QualifiedProfile` avec champs requis (§9.9 CDC) : `runtime`, `modelId`, `ctxSize`, `batchSize`, `ubatchSize`, `threads`, `threadsBatch`, `ngl`, `flashAttn`, `mlock`, `batteryPolicyRef`, `fallbackProfileRef`
- [x] DECISION DE NOMMAGE JSON A TRANCHER EN PATCH 4 — ne pas laisser coexister deux conventions :
  - Backend API (existant) : `kebab-case` dans les URL (`model-catalog.json`, `warmup-profiles.json`, `capability-state.json`)
  - CDC §5.8 (spec) : `snake_case` pour les fichiers locaux (`model_catalog.json`, `warmup_profiles.json`)
  - DECISION RECOMMANDEE : **`snake_case` pour les artefacts disque locaux** (conforme CDC, fichiers locaux client) ; **`kebab-case` conserve pour les segments de path API** (conforme backend existant). Ne jamais melanger les deux dans le meme contexte.
  - Documenter la decision dans un commentaire de `GovernanceArtifactStore.cs` pour eviter la derive future.
- [x] `GovernanceArtifactStore` : lecture/ecriture avec checksum SHA-256 (§5.9)
  - Persistance atomique avec sidecar `.sha256`
  - Startup integrity check : refus si checksum invalide, mode degrade pas crash
  - Acces ecriture reserve au code interne client
- [x] `LocalLlmBootstrapper` : initialise un `QualifiedProfile` de reference et les artefacts ; construit les args runtime depuis le profil qualifie quand il matche runtime/modele, sans ecraser les args explicites

**Tests a lancer / a ajouter** :
- [x] Test serialisation/deserialisation `QualifiedProfile` roundtrip
- [x] Test `GovernanceArtifactStore` : ecriture + relecture + verification checksum
- [x] Test startup integrity : artefact corrompu -> mode degrade (pas exception non geree)
- [x] `dotnet test backend/SAAIA.Backend.Tests/...` vert (335/335)
- [x] `dotnet test client/SAAIA.Client.ToolAgent.Tests/...` vert (262/262)

---

### Patch 5 — Warmup gate formel + rollback + blacklist + requalification

**Objectif** : implementer la machine d'etat complete de qualification machine.

**Fichiers touches** :
- `client/SAAIA.Client.WinUI/Services/WarmupGate.cs` (nouveau)
- `client/SAAIA.Client.WinUI/Services/BlacklistPolicy.cs` (nouveau)
- `client/SAAIA.Client.WinUI/Services/RollbackManager.cs` (nouveau)
- `client/SAAIA.Client.WinUI/MainWindow/LocalLlm.cs` (integration etats UX)
- `backend/SAAIA.Backend/Endpoints/AdminRuntimeEndpoints.cs` (enrichir requalify)

**Travaux — Warmup gate (§9.6)** :
- [x] Implementer warmup gate : moteur decisionnel + harnais de mesure `/models` + `/chat/completions` en place
  - Seuils lus depuis `warmup_profiles.json`, jamais hardcodes
  - Valeur de reference : TTFT < 12 000 ms = nominal GPU interactif
  - States : PASS / PASS_DEGRADED / FAIL_BLOCK / FAIL_FALLBACK
- [x] Persister resultats dans `warmup_results.json` apres chaque qualification
- [x] Consommer `/metrics` llama.cpp si expose (Prometheus opportuniste, non bloquant)
- [x] Harnais qualification (§15.5.1) : 3 passes de qualification, chacune agregant `short_ttft`, `long_prefill` et `decode_stable`

**Travaux — Blacklist et quarantaine (LLM-015, LLM-016, §9.14)** :
- [x] Consulter `blacklist.json` avant warmup et avant lancement gere par `LlamaCppProcessManager`
- [x] Refuser sans tentative tout couple blackliste
- [x] Quarantaine : checksum mismatch -> marquer `quarantined`, journaliser, bloquer sans fallback implicite
  - [x] Implémentation v1 client : renommage `*.quarantine`, journalisation `acquisition_log.json`, blocage explicite avant lancement
- [x] Exposer blacklist active en lecture seule dans interface admin

**Travaux — Rollback (LLM-014, §9.15)** :
- [x] Maintenir `last_known_good_profile.json` : dernier profil avec 3 runs consecutifs conformes
- [x] Rollback automatique vers `lastKnownGoodProfile` si echec avec profil sain disponible
- [x] Journaliser rollback avec cause et timestamp dans `rollback_log.json`

**Travaux — Requalification (§9.13)** :
- [x] Implementer les 8 triggers de requalification (driver/runtime/modele/hardware/eGPU + derive perfs/echecs repetes/timeout/action admin)
- [x] Capturer `fingerprint` machine dans `hardware_probe.json`
- [x] Comparer snapshot courant vs `hardware_probe.json` au demarrage et logguer `Requalification required` si fingerprint change
- [x] Admin UI : bouton "Requalifier" -> `POST /admin/runtime/requalify`
- [x] Admin UI : bouton "Reconcile stale" -> `POST /admin/runtime/reconcile-stale`

**Tests a lancer / a ajouter** :
- [x] Test warmup gate : PASS avec N runs conformes, PASS_DEGRADED, FAIL_BLOCK si depassement seuil
- [x] Test rollback : apres echec, profil actif = lastKnownGoodProfile si present
- [x] Test blacklist : couple blackliste refuse sans tentative de warmup
- [x] `dotnet test backend/SAAIA.Backend.Tests/...` vert (335/335)
- [x] `dotnet test client/SAAIA.Client.ToolAgent.Tests/...` vert (274/274)

**Criteres de sortie** :
- Warmup gate decisionnel operationnel, resultats dans warmup_results.json
- Blacklist consultee avant warmup et lancement gere
- Rollback automatique fonctionne et est journalise
- Reste : application UX/runtime des policies batterie et hard gate RAM optionnel

---

## Phase 3 — Suite gouvernance (dependante des Patch 4/5)

Ces items dependent des fondations posees dans Patch 4 et 5.

### Budget VRAM observe DXGI (§9.11, LLM-008)

- [x] `HardwareProbeService` : DXGI `QueryVideoMemoryInfo` via COM/PInvoke Windows (budget courant observe, pas seulement VRAM installee)
- [~] Cas UMA Intel Arc (budget partage != VRAM dediee) : capture possible via DXGI + telemetry Intel `xpu-smi`, policy de selection encore a calibrer
- [x] Detection eGPU distinct + trigger requalification si debranche
- [x] Hard gate `dxgi_budget_available` : seuil `MinDxgiBudgetMiB` lu depuis `warmup_profiles.json`, blocage avant qualification si insuffisant
- [x] Enrichir `hardware_probe.json` : vendor, nom GPU, VRAM dediee, budget DXGI courant, usage courant, RAM totale/disponible, mode batterie/secteur et fingerprint machine presents

### QoS batterie et energie (§9.12, LLM-017)

- [x] Modes Perf / Balanced / Eco : artefact `battery_policies.json` avec idle timeout AC/batterie et fallback recommande
- [~] Declencheurs : passage batterie detecte depuis `hardware_probe.json` et signale comme requalification si policy recommande fallback ; chute tok/s et temperature restent a faire
- [x] `batteryPolicyRef` inscrit dans `QualifiedProfile`

### Cycle de vie runtime sleep/wake (§9.16)

Base existante : `ManageLocalLlmProcess` + `AutoStartOnConnect` dans `AppSettings` et `SetupLifecycle.cs` gerent le lancement et l'arret manuel. Ce n'est pas un chantier zero — c'est une extension du mecanisme en place.

Ce qui manque pour le contrat CDC :
- [x] `LlamaCppProcessManager` : `idleTimeoutSeconds` resolu depuis `warmup_profiles.json` / `battery_policies.json` + timer d'inactivite -> arret propre et log du sleep
- [x] Drain avant sleep : heartbeat `RuntimeActivityStarted/Finished` bloque l'arret tant qu'une requete LLM est active
- [x] `AppSettings` : flag `EagerLoad` (clarifie l'ancien `AutoStartOnConnect` dans la logique warmup)
- [x] Wake sur demande : si `EagerLoad = false` ou apres sleep, demarrage du runtime juste avant generation
- [~] Eco/Balanced/Perf : `idleTimeoutSeconds` resolu depuis `battery_policies.json` via `BatteryPolicyRef` ; selection/calibration Eco explicite reste a finaliser

### UX etats runtime LLM (§14.3) — contractuel

- [x] Verifier dans `MainWindow/LocalLlm.cs` les etats suivants (absents = bug UX) :
  - [x] Wake en cours -> "Chargement du modele en cours..." (Info)
  - [x] Warmup en cours -> "Verification de compatibilite en cours..." (Info)
  - [x] Pret (profil nominal) -> aucun message (transparent)
  - [x] Profil degrade actif -> "Mode performance reduite actif." (Avertissement)
  - [x] Fallback actif -> "Profil de secours actif." (Avertissement)
  - [x] Generation en cours -> indicateur streaming visible dans la bulle assistant, sans nouvelle vue surchargee
  - [x] Erreur warmup -> "Assistant temporairement indisponible." (Erreur)
  - [x] Mismatch checksum / quarantaine -> "Modele non disponible — contactez l'administrateur" (Erreur)
  - [x] Requalification necessaire -> ligne de statut LLM existante (sans nouvelle vue surchargee)

### Telemetrie GPU multi-vendor (§15.2.1)

- [x] AMD SMI / ROCm SMI : VRAM, temperature, frequence
- [x] Intel Level Zero : budget memoire, utilisation, UMA via probe `xpu-smi` opportuniste
- [x] Endpoint `/metrics` llama.cpp consomme opportunistiquement par `LocalLlmWarmupHarness` ; mapping canonique tokens/KV cache/threads formalise en cles `runtime.*`
- [x] Regle : pas de collecte a chaque requete user ; collecte `/metrics` limitee au warmup/qualification, `hardware_probe` reste le snapshot hardware

### Support bundle gouvernance enrichi (§14.2)

- [x] Quand les artefacts governance existent (Patch 4/5) : enrichir `POST /admin/support/bundle` avec les 9 artefacts contractuels (hardware_probe, capability_state, warmup_results, last_known_good_profile, rollback_log, blacklist_applied, acquisition_log, logs LLM, config redactee)

### Harnais regression CI (§15.5.2)

- [x] Harnais CI distinct du harnais qualification : concurrence backend, recovery crash, profil degrade, reproductibilite TTFT/tok/s dans les marges de `warmup_profiles.json`

---

## Validation obligatoire apres chaque patch

**Backend :**
- [x] `dotnet build backend/SAAIA.Backend/SAAIA.Backend.csproj`
- [x] `dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj -p:NuGetAudit=false`

**Client (obligatoire pour les Patch 1, 2, 4, 5 qui touchent le client WinUI) :**
- [x] `dotnet build client/SAAIA.Client.WinUI/SAAIA.Client.WinUI.csproj -p:Platform=x64 -p:Configuration=Debug`
- [x] `dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj -p:NuGetAudit=false`

**Regles transverses :**
- [x] Si retrieval change : non applicable a cette passe
- [x] Si endpoint ou artefact change : tests contractuels associes mis a jour
- [x] Si profil qualifie change : non applicable a cette passe
- [x] Mettre a jour ce `TODO.md`

---

## Sprints clos (historique)

### Sprint 1 - Quick wins evidence pack

- [x] Offsets derives `offsetStart` / `offsetEnd`
- [x] `hypQuestionsMatched` branche et verrouille par tests explicites
- [x] Backfill legacy route via Cap A

### Sprint 2 - Coherence cache HTTP

- [x] ETag/304 ajoutes sur `catalog/categories`, `catalog/documents`, `documents/stats`
- [x] Statut produit de `/catalog/snapshot` tranche et documente

### Sprint 3 - Dette architecturale runtime

- [x] Refactor monolithe `RuntimeGovernanceService`
- [x] Services read / command / gate / diagnostics extraits
- [x] Contrats top-level JSON verrouilles sur endpoints et artefacts runtime
- [x] Validation Sprint 3.2 etendue aux flux A/B operationnels et KPI A

### Sprint 4 - Capacite A

- [x] Questions hypothetiques LLM + fallback deterministe
- [x] Tags suggeres LLM + fallback deterministe
- [x] Scoring qualite A, warmup A, KPI A, pilotage client dedie, vue admin

### Sprint 5 - Capacite B

- [x] Generation LLM locale worker, fallback deterministe, fallback live
- [x] Probe warmup / runtime B, KPI B, telemetrie, score qualite, revue qualite corrective

### Phase 0A — Quick fixes LLM bench-confirmed (CDC v3.1 §18.4)

- [x] `GgufMetadataReader` : lecteur binaire GGUF (block_count, head_count_kv), support v1/v2/v3, cap 2 MB
- [x] `GpuDetector.ComputeAutoTuning` : ngl = llm.block_count depuis GGUF ; fallback tier VRAM avec warning
- [x] `GpuDetector.ComputeAutoTuning` : batch >= 512 pour tout profil GPU (LLM-009)
- [x] `AppSettings.ExtraArgs` default : `"--ctx-size 3072"` (CDC LLM-007)
- [x] `MainWindow.xaml` placeholder : `"--ctx-size 3072"`
- [x] `AppSettings` : nouveaux champs `UbatchSize=256`, `ThreadsBatch=6`, `FlashAttn=null` (LLM-010/011)
- [x] `ApplyAutoTuningFlags` : injection `--ubatch-size`, `--threads-batch`, `--flash-attn on/off` (CUDA uniquement)
- [x] `LlamaCppProcessManager` : ligne de commande complete loggee via ClientLog.Info au demarrage
- [x] Nettoyage legacy `strictMode` : AppSettings (LocalSettings + JSON) + Provisioning.LegacyStrictMode

### Phase 0B — Securisation runtime (CDC v3.1 §14.2)

- [x] `POST /admin/support/bundle` : endpoint backend ZIP with staging + auth + artifacts/missingArtifacts
- [x] Warning logge si `Sha256Hex = null` pour un modele connu (LLM-008)

### Phase 3 / Patch 4 — Socle gouvernance modeles client (CDC v3.1 §5.8 / §9)

- [x] Artefacts locaux `snake_case` sous `%LOCALAPPDATA%\SAAIA\governance`
- [x] Stores `GovernanceArtifactStore`, `ModelCatalogStore`, `WarmupProfileStore`
- [x] `QualifiedProfile` de reference : profil B interactif + profil C fallback
- [x] Sidecars `.sha256` et lecture degradee si corruption
- [x] Tests client ajoutes : roundtrip profil, checksum, corruption, creation defaults

### Phase 3 / Patch 5 — Warmup gate decisionnel + rollback + blacklist (CDC v3.1 §9.6 / §9.14 / §9.15)

- [x] `WarmupGate` : PASS / PASS_DEGRADED / FAIL_BLOCK / FAIL_FALLBACK depuis mesures fournies
- [x] `LocalLlmWarmupHarness` : mesure readiness/load via `/models`, TTFT et tok/s via `/chat/completions`, capture `/metrics` si expose
- [x] `WarmupGate.RunQualificationAsync` : execute N runs puis applique seuils / rollback / blacklist
- [x] `BlacklistPolicy` : match runtime/model/profile/driver avant warmup
- [x] `RollbackManager` : last-known-good + journal `rollback_log.json`
- [x] `LlamaCppProcessManager` refuse un profil actif blackliste avant lancement
- [x] Tests client ajoutes : PASS, degraded, block, fallback, blacklist, harnais streaming/non-streaming/not-ready/metrics
- [x] Harnais contractuel multi-scenarios : prompt court TTFT, prompt long prefill/contexte, prompt decode stable, avec agregat worst-case
- [x] `hardware_probe.json` : capture RAM/GPU/fingerprint + budget DXGI opportuniste avec fallback degrade
- [x] Hard gate memoire GPU : `MinDxgiBudgetMiB` dans les profils warmup + refus `hard_gate_dxgi_budget_insufficient`
- [x] Trigger requalification hardware : comparaison fingerprint courant vs `hardware_probe.json` au bootstrap
- [x] Policy batterie : `battery_policies.json` + evaluation `client-balanced` -> fallback stable sur batterie
- [x] Triggers requalification modele/runtime : derive detectee au bootstrap depuis `QualifiedProfile` vs runtime/modeles courants
- [x] Trigger requalification driver : comparaison `gpuDriverVersion` courant vs `hardware_probe.json`
- [x] Application runtime v1 : `EagerLoad` branche sur le connect/startup et `idleTimeoutSeconds` pilote par profil/policy avec heartbeat d'activite LLM
- [x] Checksums de reference reels renseignes pour les 8 modeles locaux disponibles

### Phase 3 / Patch 6 — Runtime compatibility policy + upgrade versionne (CDC v3.1 §5.8 / §9)

**Objectif** : traiter `llama.cpp` comme un artefact produit gouverne, pas comme un simple `llama-server.exe` present sur disque.

**Travaux** :
- [x] Ajouter `runtime_compatibility_policy.json` aux artefacts locaux suivis avec sidecar `.sha256`
- [x] Decrire les runtimes approuves par `runtimeId`, `build`, backend et architectures GGUF supportees
- [x] Ajouter la regle Gemma 4 : `gemma4` exige `llama.cpp-cuda`/`llama.cpp-cpu >= b8901`
- [x] Ajouter override machine : `gemma4 + llama.cpp-cuda + Pascal => flash-attn=false`
- [x] Brancher l'override dans `ApplyAutoTuningFlags` pour eviter le crash `flash-attn on` observe sur Quadro P520
- [x] Installer les runtimes dans des dossiers versionnes (`win-cuda-x64/b8149`, `win-cuda-x64/b8901`) au lieu d'ecraser le dossier actif
- [x] Ajouter un `active-runtime.json` qui pointe vers le runtime actif et conserve le dernier runtime sain
- [x] Rendre `LlamaCppReleaseDownloader` version-aware : `present` ne suffit plus, verifier `runtime.tag`, checksum, architecture demandee et build minimal
- [x] Ajouter rollback runtime si le warmup gate echoue apres upgrade
- [x] Exposer le diagnostic runtime actif / build requis / upgrade requis dans le support bundle et la vue admin runtime
  - [x] Support bundle : `active-runtime.json`, `runtime_event_log.json`, `runtime_event_log.txt`, chemins runtime actifs et tags CPU/CUDA/Vulkan
  - [x] Statut client : message explicite `Mise a niveau du runtime requise.`
  - [x] Vue runtime locale : overlay dedie avec build actif / requis, etat pending/qualified, warmup, profil qualifie, policy flash-attn, historique runtime recent et actions `Mettre a niveau` / `Demarrer et qualifier`
  - [x] Vue admin/runtime : resume runtime local integre dans l'overlay admin sans surcharge de la vue, avec timestamps d'activation/qualification et 3 derniers evenements

**Decision produit** :
- Gemma 4 reste famille de test tant que le runtime SAAIA embarque officiel n'est pas upgrade et qualifie par warmup.
- Qwen reste le chemin nominal client tant que son profil qualifie garde le meilleur compromis stabilite / TTFT / tok/s.

### Gaps fermes

- [x] `hypQuestionsMatched` verrouille par deux tests explicites
- [x] Vue `KPI A` resiliente si `operational-summary` echoue
- [x] Contrats JSON top-level renforces sur les flux runtime A/B
- [x] Wire LLM durci : `LocalLlmChatClient` couvre payloads invalides (content[], choices, JSON invalide)
- [x] Baseline artefacts runtime/admin versionnee : `runtime_admin_artifacts_baseline.v1.json`, 333 tests verts
- [x] `POST /documents/resolve-category` present (Phase 0 API drift resolu)
- [x] `POST /sources/resolve` present (Phase 0 API drift resolu)
- [x] `POST /admin/runtime/requalify` present (surface admin runtime stabilisee)

---

## Historique recent

| Date | Intervenant | Action |
|---|---|---|
| 2026-04-22 | Codex | Cap A ops : endpoint/artifact `capability-a-kpis` + tests contractuels runtime |
| 2026-04-22 | Codex | Client admin : vue fille `KPI A`, live ops et dry-run offsets |
| 2026-04-22 | Codex | Fermeture P3 : tests `hypQuestionsMatched` et degradation partielle `KPI A` |
| 2026-04-22 | Codex | Sprint 3.2 : baseline top-level JSON etendue aux flux runtime A/B |
| 2026-04-22 | Codex | Wire LLM durci + baseline artefacts runtime/admin versionnee (333 tests verts) |
| 2026-04-23 | Assistant IA | Mise a jour TODO : analyse drift CDC v3.1, LLM-005 a LLM-018, gouvernance modeles |
| 2026-04-23 | Assistant IA | Restructuration TODO : renommage scope Client+Backend, Phase 0A/0B, 5 patchs atomiques |
| 2026-04-23 | Codex | Patch 4 socle : artefacts gouvernance client, `QualifiedProfile`, checksums, 262 tests client verts |
| 2026-04-23 | Codex | Patch 5 socle : warmup gate decisionnel, blacklist, rollback, 267 tests client verts |
| 2026-04-23 | Codex | Warmup UX/client : demarrage manuel aligne sur le warmup gate, statut nominal transparent, 290 tests client verts |
| 2026-04-23 | Codex | Quarantaine checksum modele : blocage pre-start, renommage `.quarantine`, journal `acquisition_log.json`, statut UX dedie, 293 tests client verts |
| 2026-04-23 | Codex | Reference checksum Qwen2.5 3B Q4_K_M renseignee dans `model_catalog.json`/bootstrap, 294 tests client verts |
| 2026-04-23 | Codex | Wake-on-demand : `LastStartupLoadMs` mesure le vrai chargement et l'injecte dans `warmup_results.json` via `runtime.observed_start_load_ms` |
| 2026-04-23 | Codex | Script `tools/compute-model-reference-checksums.ps1` ajoute ; etat machine confirme : 1 modele pack trouve, 5 manquants |
| 2026-04-23 | Codex | `GovernanceArtifactStore` upgrade maintenant `model_catalog.json` local quand un checksum de reference devient connu ; script checksum sait aussi verifier le catalogue local, 295 tests client verts |
| 2026-04-23 | Codex | Script checksum : `catalogStatus` explicite (`missing`/`loaded`/`updated`), sortie d'erreur dediee si update demande sans catalogue local present |
| 2026-04-23 | Codex | Script checksum : bootstrap local `model_catalog.json` si absent (baseline Qwen actuel), verification locale ensuite en `catalog_match` pour le modele present |
| 2026-04-23 | Codex | Le chargement UI LLM initialise maintenant les artefacts locaux de gouvernance au demarrage, sans attendre warmup/bootstrap ulterieur |
| 2026-04-23 | Codex | Support bundle admin enrichi avec artefacts gouvernance v3.1 + logs LLM + config redigee ; test ZIP contractuel vert |
| 2026-04-23 | Codex | Lecteur GGUF corrige pour cles prefixees architecture (`qwen2.*`) et valide sur le modele reel Qwen2.5 3B Q4_K_M ; 300 tests client verts |
| 2026-04-23 | Codex | Detection eGPU v1 : `gpuIsExternal`/`gpuConnectionHint` dans `hardware_probe.json` et trigger `external_gpu_disconnected` teste |
| 2026-04-23 | Codex | Harnais CI runtime distinct ajoute (`tools/runtime-ci-harness.ps1`) : tests .NET, probes runtime optionnelles, concurrence/recovery/reproductibilite TTFT/tok/s |
| 2026-04-23 | Codex | Telemetrie vendor GPU v1 : probes optionnelles AMD `amd-smi`/`rocm-smi` et Intel `xpu-smi`, parsing JSON teste dans `hardware_probe.json` |
| 2026-04-23 | Codex | `LocalLlmBootstrapper` applique les flags depuis `QualifiedProfile` valide (`ctx`, threads, batch, ubatch, ngl, flash-attn, mlock) en preservant les args utilisateur |
| 2026-04-23 | Codex | Mapping `/metrics` llama.cpp formalise : conservation des cles brutes + projection canonique `runtime.*` pour tokens, KV cache, threads et slots |
| 2026-04-23 | Codex | UX streaming LLM : indicateur leger `Generation en cours...` dans la bulle assistant, pilote par `ChatMessageItem.IsStreaming` |
| 2026-04-23 | Codex | Triggers requalification complets : derive perf, echecs repetes, timeout et action admin ajoutes au service decisionnel + statut local |
| 2026-04-23 | Codex | Checksums modeles locaux : 8 GGUF verifies, `model_catalog.json` et auto-selection bootstrap alignes sur Qwen Q4/Q6/Q8 + Mistral IQ3/Q4 ; Gemma reste famille de test |
| 2026-04-23 | Codex | Gemma 4 prepare en famille de test Apache 2.0 : E2B Q4_K_M, E2B Q8_0 et E4B Q4_K_M verifies localement, catalogue/sources/checksums alignes |
| 2026-04-23 | Codex | Bench Gemma 4 avec runtime llama.cpp b8901 isole : runtime SAAIA b8149 ne supporte pas `gemma4`; E2B Q8_0 OK en `ngl=16`/flash-off (~0.8s TTFT apres warm, ~7.4 tok/s), flash-on plante sur Pascal, E4B Q4_K_M trop lent (~1.65 tok/s) |
| 2026-04-23 | Codex | Bench Gemma 4 E2B Q4_K_M ajoute : profil `ngl=24`/flash-off recommande sur Quadro P520 (~0.57s TTFT warm, ~12.3 tok/s), meilleur candidat Gemma local mais non retenu par defaut tant que runtime SAAIA embarque ne supporte pas `gemma4` |
| 2026-04-23 | Codex | Runtime compatibility policy v1 : artefact `runtime_compatibility_policy.json`, regle Gemma4 >= b8901, override Pascal `flash-attn=false`, tests contractuels verts |
| 2026-04-23 | Codex | Downloader runtime version-aware : installation versionnee sous `llm/runtime/<backend>/<build>`, `active-runtime.json`, support bundle enrichi et statut client `runtime_upgrade_required`, 312 tests client verts |
| 2026-04-23 | Codex | Rollback runtime apres upgrade : `active-runtime.json` passe en `pending_qualification`, le warmup qualifie le nouveau runtime ou restaure automatiquement le build precedent sain, 314 tests client verts |
| 2026-04-23 | Codex | Diagnostic runtime local : service dedie + overlay UI avec build actif / requis, etat runtime, warmup et override flash-attn ; 316 tests client verts |
| 2026-04-23 | Codex | Overlay admin runtime : resume compact du runtime local ajoute (build actif/requis, etat, warmup, flash-attn), sans surcharger la vue existante |
| 2026-04-23 | Codex | Overlay runtime local actionnable : upgrade runtime declenchable depuis le diagnostic, puis qualification/warmup relancable sans sortir de la vue |
| 2026-04-23 | Codex | Confirmation avant upgrade runtime + historique simple visible (active/qualifie le) dans les vues locale et admin ; build WinUI 0 warning, 316 tests client verts |
| 2026-04-23 | Codex | Runtime event log local : artefact `runtime_event_log.json`, evenements d'upgrade/qualification/rollback/confirmation, support bundle enrichi et diagnostics alimentes ; 317 tests client verts |
| 2026-04-23 | Codex | Lecture runtime plus exploitable : 3 derniers evenements visibles dans l'overlay admin et resume texte `runtime_event_log.txt` ajoute au support bundle |
| 2026-04-23 | Codex | Passe multilingue runtime : overlay diagnostic local, resume admin runtime, actions visibles du flyout LLM local et statuts runtime alignes sur `UiLanguage` ; build WinUI 0 warning, 317 tests client verts |
| 2026-04-23 | Codex | Passe multilingue et audit client elargis : setup wizard, parcours d'installation/connect, sources cards, infos modele local et erreurs techniques ApiClient/ModelLibrary alignes sur `UiLanguage` ; build WinUI 0 warning, 317 tests client verts |
| 2026-04-23 | Codex | Fenetre principale finalisee cote multilingue : flyout LLM local, copy tooltips, indicateur streaming, titres sources et libelles techniques caches alignes sur `UiLanguage` ; build WinUI 0 warning, 317 tests client verts |
