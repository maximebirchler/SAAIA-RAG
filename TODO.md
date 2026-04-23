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
| Gouvernance llama-server client | Client | [~] Patch 5 avance | Artefacts locaux, `QualifiedProfile`, checksums, warmup gate, harnais TTFT/tok/s multi-scenarios, rollback, blacklist, hardware_probe, policy batterie et triggers hardware/driver/runtime/modele poses ; runtime v1, statuts UX et quarantaine checksum modele en place |
| Cycle de vie runtime (sleep/wake) | Client | [x] Runtime v1 | `EagerLoad` explicite, idle timeout pilote par profil/policy, drain via heartbeat et wake a la demande avant generation |
| Checksums modeles | Client | [~] Partiel | Infrastructure SHA-256 presente ; warning logge si Sha256Hex=null (Patch 3) ; mismatch connu -> quarantaine `.quarantine` + journal `acquisition_log.json` ; Qwen2.5 3B Q4_K_M reference SHA-256 renseigne, autres modeles pack encore a calculer |
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
- [x] `dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj -p:NuGetAudit=false -nologo` — 294/294 verts
- [x] `git diff --check` sans nouvelle erreur bloquante
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
- [~] Test unitaire `GgufMetadataReader` : lire le modele present sur la machine de dev, verifier `block_count == 36` et `head_count_kv == 2` pour Qwen 2.5 3B Q4_K_M
  > Squelette fourni dans CODEX-BRIEF-PHASE0.md — necessite le .gguf sur la machine de dev
- [~] Test `ComputeAutoTuning` : verifier que ngl provient du GGUF, que batch >= 512 sur GPU, que le fallback est safe si GGUF illisible
  > Squelette fourni dans CODEX-BRIEF-PHASE0.md — necessite le .gguf sur la machine de dev
- [x] `dotnet test backend/SAAIA.Backend.Tests/...` vert (333/333) — passe

**Criteres de sortie** :
- [x] ngl n'est plus une constante, il provient du GGUF
- [x] batch >= 512 pour tout profil CUDA
- [x] ctx default = 3072
- [~] tests GGUF : squelettes fournis, a jouer avec le .gguf reel sur la machine cible

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
- [~] CHECKSUMS MODELES :
  - [x] Warning logge si `Sha256Hex = null` (ne bloque pas le telechargement en Phase 0B)
  - [~] Renseigner les SHA-256 reels pour Qwen2.5-3B-Instruct-Q4_K_M.gguf et autres modeles du pack
    > Qwen2.5-3B-Instruct-Q4_K_M.gguf renseigne : `9c9f56a391a3abbd5b89d0245bf6106081bcc3173119d4229235dd9d23253f94`
    > Restent a calculer sur machine de reference : Q4_0, Q4_K_S, Q6_K, Mistral Q4_K_M, Mistral Q6_K
    > Script local ajoute : `tools/compute-model-reference-checksums.ps1` pour scanner `models/` + `%LOCALAPPDATA%\\SAAIA\\Models` et produire l'etat reel des hashes disponibles
- [x] `SupportBundleBuilder` (client leger) : pas de changement en Patch 3 — Phase 3 l'enrichira quand les artefacts governance existeront

**Tests a lancer / a ajouter** :
- [x] Test contractuel `POST /admin/support/bundle` : status 200, champ `bundlePath` present, response JSON conforme
- [x] Test auth : sans contexte admin -> rejet `UnauthorizedAccessException`
- [x] `dotnet test backend/SAAIA.Backend.Tests/...` vert (335/335) — passe

**Criteres de sortie** :
- [x] `POST /admin/support/bundle` repond 200 avec bundle (meme partiel)
- [x] Test contractuel backend ajoute
- [~] Checksums : warning en place ; valeurs reelles SHA-256 a calculer lors du bench suivant
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
- [~] `LocalLlmBootstrapper` : initialise un `QualifiedProfile` de reference et les artefacts ; la construction complete des args depuis le profil reste a finir avec le warmup gate Patch 5

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
- [~] Quarantaine : checksum mismatch -> marquer `quarantined`, journaliser, bloquer sans fallback implicite
  - [x] Implémentation v1 client : renommage `*.quarantine`, journalisation `acquisition_log.json`, blocage explicite avant lancement
- [ ] Exposer blacklist active en lecture seule dans interface admin

**Travaux — Rollback (LLM-014, §9.15)** :
- [x] Maintenir `last_known_good_profile.json` : dernier profil avec 3 runs consecutifs conformes
- [x] Rollback automatique vers `lastKnownGoodProfile` si echec avec profil sain disponible
- [x] Journaliser rollback avec cause et timestamp dans `rollback_log.json`

**Travaux — Requalification (§9.13)** :
- [~] Implementer les 8 triggers de requalification (driver/runtime/modele/hardware faits ; derive perfs, echecs repetes, timeout, action admin restent a faire)
- [x] Capturer `fingerprint` machine dans `hardware_probe.json`
- [x] Comparer snapshot courant vs `hardware_probe.json` au demarrage et logguer `Requalification required` si fingerprint change
- [ ] Admin UI : bouton "Requalifier" -> `POST /admin/runtime/requalify`

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
- [~] Cas UMA Intel Arc (budget partage != VRAM dediee) : capture possible via DXGI, policy de selection encore a calibrer
- [ ] Detection eGPU distinct + trigger requalification si debranche
- [x] Hard gate `dxgi_budget_available` : seuil `MinDxgiBudgetMiB` lu depuis `warmup_profiles.json`, blocage avant qualification si insuffisant
- [x] Enrichir `hardware_probe.json` : vendor, nom GPU, VRAM dediee, budget DXGI courant, usage courant, RAM totale/disponible, mode batterie/secteur et fingerprint machine presents

### QoS batterie et energie (§9.12, LLM-017)

- [x] Modes Perf / Balanced / Eco : artefact `battery_policies.json` avec idle timeout AC/batterie et fallback recommande
- [~] Declencheurs : passage batterie detecte depuis `hardware_probe.json` et signale comme requalification si policy recommande fallback ; chute tok/s, temperature, eGPU debranche restent a faire
- [ ] `batteryPolicyRef` inscrit dans `QualifiedProfile`

### Cycle de vie runtime sleep/wake (§9.16)

Base existante : `ManageLocalLlmProcess` + `AutoStartOnConnect` dans `AppSettings` et `SetupLifecycle.cs` gerent le lancement et l'arret manuel. Ce n'est pas un chantier zero — c'est une extension du mecanisme en place.

Ce qui manque pour le contrat CDC :
- [x] `LlamaCppProcessManager` : `idleTimeoutSeconds` resolu depuis `warmup_profiles.json` / `battery_policies.json` + timer d'inactivite -> arret propre et log du sleep
- [x] Drain avant sleep : heartbeat `RuntimeActivityStarted/Finished` bloque l'arret tant qu'une requete LLM est active
- [x] `AppSettings` : flag `EagerLoad` (clarifie l'ancien `AutoStartOnConnect` dans la logique warmup)
- [x] Wake sur demande : si `EagerLoad = false` ou apres sleep, demarrage du runtime juste avant generation
- [ ] Eco : `idleTimeoutSeconds` reduit selon politique QoS (§9.12)

### UX etats runtime LLM (§14.3) — contractuel

- [~] Verifier dans `MainWindow/LocalLlm.cs` les etats suivants (absents = bug UX) :
  - [x] Wake en cours -> "Chargement du modele en cours..." (Info)
  - [x] Warmup en cours -> "Verification de compatibilite en cours..." (Info)
  - [x] Pret (profil nominal) -> aucun message (transparent)
  - [x] Profil degrade actif -> "Mode performance reduite actif." (Avertissement)
  - [x] Fallback actif -> "Profil de secours actif." (Avertissement)
  - [~] Generation en cours -> indicateur streaming visible
  - [x] Erreur warmup -> "Assistant temporairement indisponible." (Erreur)
  - [x] Mismatch checksum / quarantaine -> "Modele non disponible — contactez l'administrateur" (Erreur)
  - [x] Requalification necessaire -> ligne de statut LLM existante (sans nouvelle vue surchargee)

### Telemetrie GPU multi-vendor (§15.2.1)

- [ ] AMD SMI / ROCm SMI : VRAM, temperature, frequence
- [ ] Intel Level Zero : budget memoire, utilisation, UMA
- [ ] Endpoint `/metrics` llama.cpp (tokens, latences, KV cache, threads)
- [ ] Regle : telemetrie collectee uniquement au hardware_probe (pas a chaque requete)

### Support bundle gouvernance enrichi (§14.2)

- [ ] Quand les artefacts governance existent (Patch 4/5) : enrichir `POST /admin/support/bundle` avec les 9 artefacts contractuels (hardware_probe, capability_state, warmup_results, last_known_good_profile, rollback_log, blacklist_applied, acquisition_log, logs LLM, config redactee)

### Harnais regression CI (§15.5.2)

- [ ] Harnais CI distinct du harnais qualification : concurrence backend, recovery crash, profil degrade, reproductibilite TTFT/tok/s dans les marges de `warmup_profiles.json`

---

## Validation obligatoire apres chaque patch

**Backend :**
- [ ] `dotnet build backend/SAAIA.Backend/SAAIA.Backend.csproj`
- [ ] `dotnet test backend/SAAIA.Backend.Tests/SAAIA.Backend.Tests.csproj -p:NuGetAudit=false`

**Client (obligatoire pour les Patch 1, 2, 4, 5 qui touchent le client WinUI) :**
- [ ] `dotnet build client/SAAIA.Client.WinUI/SAAIA.Client.WinUI.csproj -p:Platform=x64 -p:Configuration=Debug`
- [ ] `dotnet test client/SAAIA.Client.ToolAgent.Tests/SAAIA.Client.ToolAgent.Tests.csproj -p:NuGetAudit=false`

**Regles transverses :**
- [ ] Si retrieval change : rejouer les regressions retrieval / harness
- [ ] Si endpoint ou artefact change : mettre a jour les tests contractuels associes
- [ ] Si profil qualifie change : mettre a jour `warmup_profiles.json` + relancer harnais qualification
- [ ] Mettre a jour ce `TODO.md`

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
- [~] Reste a faire : checksums de reference reels restants

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
