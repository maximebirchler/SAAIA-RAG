# Reprise SAAIA RAG - Cuisine / ingestion / retrieval

Date de reprise: 2026-05-15  
Branche: `SAAIA_V3.1`  
Depot: `C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG`  
Serveur cible: `saaia-server` / `https://saaia-server.taila2196b.ts.net`

## Objectif initial

Fermer proprement la qualite RAG sur la categorie exemple `Cuisine`, qui sert de base aux futures categories metier.

L'objectif n'est pas de coder des regles propres aux recettes ou a des PDF precis. Le produit doit rester generique, multilingue et adaptatif a l'arborescence et aux documents indexes. La categorie Cuisine sert uniquement de corpus de test exigeant.

Les axes principaux etaient:

- ameliorer l'ingestion et les profils documentaires pour reduire le bruit;
- exploiter les profils, content cards, resume et signaux OCR dans la recuperation;
- fiabiliser `/rag/search` sous charge et avec des questions larges;
- eviter les reponses sans source ou les sources hors sujet;
- tester progressivement les 294 questions Cuisine, puis plus tard les reponses LLM client de bout en bout.

## Etat actuel du projet

Le depot local et distant sont synchronises sur `SAAIA_V3.1`.

Derniers commits importants:

- `99f4eefd rag: treat corpus existence queries as broad retrieval`
- `8756b0eb rag: tolerate profile array materialization`
- `f97d4621 rag: filter residual OCR profile card fragments`
- `fb39d8cf client: remove corpus-specific fact cleanup`
- `8a3d3e1f rag: keep ingestion heuristics domain neutral`
- `a5fece8f client: merge source profile signals robustly`
- `c90c8c0c rag: retain profile signals across summaries`

Etat serveur verifie:

- `/ready` OK
- `db=true`
- `tei=true`
- `qdrant=true`
- `llm=server-ok`
- `rag_search_active=0`
- `rag_search_queued=0`
- `rag_search_available_slots=4`

Le backend a ete redeploye sur `saaia-server` apres les deux derniers correctifs.

## Fichiers modifies

Fichiers modifies et deja commits/pousses:

- `backend/SAAIA.Backend/Pdf/DocumentProfileProjector.cs`
- `backend/SAAIA.Backend.Tests/DocumentProfileProjectorTests.cs`
- `backend/SAAIA.Backend/Endpoints/RagEndpoints.cs`
- `backend/SAAIA.Backend.Tests/RetrievalRuntimeSwitchTests.cs`
- `tools/run-llm-validation.ps1`

Fichier ajoute pour cette reprise:

- `REPRISE-2026-05-15-rag-cuisine.md`

## Changements deja appliques

### 1. Filtrage OCR generique des cartes de profil

Commit: `f97d4621`

Le projecteur de profils documentaires filtre maintenant mieux les faux titres/cartes issus d'OCR ou de fragments pauvres:

- fragments courts type `PY I`;
- titres majoritairement majuscules commençant par un connecteur, par exemple `DES ...`;
- fragments qui ressemblaient a des titres mais etaient en realite des bouts de texte OCR.

Les cas constates dans Cuisine:

- `facilitemps.pdf`: faux titre `DES ...`;
- `si-on-cuisinait.pdf`: faux titre `PY I`.

Le correctif reste generique et ne contient aucun nom de recette/PDF.

### 2. Reindex cible apres filtrage OCR

Documents reindexes:

- `Cuisine/facilitemps.pdf`
- `Cuisine/si-on-cuisinait.pdf`

Jobs constates comme termines:

- `6fadc9a7-4a96-41ad-9a2f-7ba83384882f`
- `c5ec9d97-b84d-4c0b-aa9a-c160dde2201f`

QA ingestion apres reindex:

- `ok: true`
- `issues: []`
- plus de warning `suspicious_profile_card_title`

Warnings restants connus:

- `navigation chunks without navigation entries` pour `Cuisine/chefbot_livre_de_recettes_fr.pdf`
- `poor extraction page ratio` pour plusieurs PDFs Cuisine, notamment `facilitemps.pdf`, `nobilia-recettes-internationales-FR.pdf`, `Je_cuisine_simplement.pdf`, etc.

Ces warnings ne bloquent pas le RAG, mais ils restent des pistes d'amelioration ingestion/OCR.

### 3. Correction d'un crash backend sur profils documentaires

Commit: `8756b0eb`

Pendant la campagne complete, beaucoup de requetes retournaient HTTP 500. Les logs backend montraient:

```text
System.InvalidOperationException:
A parameterless default constructor or one matching signature (...) is required for
SAAIA.Backend.Endpoints.RagEndpoints+DocumentProfileMatchRow materialization
```

Cause:

- PostgreSQL renvoie les colonnes `text[]` a Dapper comme `System.Array`;
- le record C# `DocumentProfileMatchRow` attendait `string[]?`;
- la materialisation Dapper echouait et faisait tomber `/rag/search`.

Correctif:

- les champs `Keywords`, `Entities`, `Topics`, `HypotheticalQuestions`, `Limits` utilisent maintenant `Array?`;
- conversion en `IReadOnlyList<string>` uniquement lors de la construction des signaux de profil;
- les retrievers de profils documentaires se degradent sur `InvalidOperationException` comme ils le faisaient deja sur `PostgresException`, au lieu de casser tout l'endpoint.

Impact:

- les HTTP 500 lies aux profils documentaires sont corriges;
- les tests backend complets repassent.

### 4. Correction des questions d'existence/inventaire corpus

Commit: `99f4eefd`

La question:

```text
Est-ce qu'il y a des recettes internationales dans les PDF ?
```

pouvait retourner 0 source avec `topK=8`, alors que le PDF `nobilia-recettes-internationales-FR.pdf` etait bien trouve avec `topK=10`.

Cause:

- la question est large, mais elle etait traitee trop proche d'une recherche focalisee;
- la selection/coupure pouvait eliminer les bonnes sources;
- le fallback profil/corpus n'etait pas declenche.

Correctif generique:

- les intentions de type `est-ce qu'il y a`, `are there`, `is there`, `do we have`, `gibt es`, `hay`, `ci sono`, etc. sont traitees comme des intentions larges quand elles visent des documents/PDF/sources/corpus;
- tests ajoutes en FR/EN/DE;
- aucun terme cuisine, recette ou PDF concret n'a ete code en dur.

### 5. Correction du harness de validation

Fichier: `tools/run-llm-validation.ps1`

Le harness envoyait:

```json
{ "category": "Cuisine" }
```

Or l'API runtime fonctionne mieux avec l'arborescence:

```json
{ "categoryPath": "Cuisine" }
```

Le script utilise maintenant `categoryPath` quand `-Category Cuisine` est fourni.

Impact:

- les campagnes de validation testent maintenant le comportement produit reel;
- les categories sont scopees par dossier, ce qui correspond a l'approche dynamique voulue.

## Points valides

### Tests backend

Commandes validees:

```powershell
dotnet test backend\SAAIA.Backend.Tests\SAAIA.Backend.Tests.csproj --filter "FullyQualifiedName~DocumentProfileProjectorTests|FullyQualifiedName~ProductRuntimeDomainNeutralityTests" --no-restore
```

Resultat: `57/57`

```powershell
dotnet test backend\SAAIA.Backend.Tests\SAAIA.Backend.Tests.csproj --filter "FullyQualifiedName~DocumentFoundationIntegrationTests|FullyQualifiedName~RetrievalRuntimeSwitchTests|FullyQualifiedName~DocumentProfileProjectorTests" --no-restore
```

Resultat: `511/511`

```powershell
dotnet test backend\SAAIA.Backend.Tests\SAAIA.Backend.Tests.csproj --filter "FullyQualifiedName~RetrievalRuntimeSwitchTests" --no-restore
```

Resultat: `371/371`

```powershell
dotnet test backend\SAAIA.Backend.Tests\SAAIA.Backend.Tests.csproj --no-restore
```

Resultats obtenus:

- avant le dernier ajout: `1184/1184`
- apres le dernier ajout: `1187/1187`

### Diff / git

```powershell
git diff --check
git status --short --branch
git push origin SAAIA_V3.1
```

Etat avant cette note:

- branche propre;
- `Everything up-to-date`;
- commits deja pousses.

### Deploiement serveur

Commande utilisee:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File infra\scripts\prod\deploy-remote-backend.ps1 -ContextName saaia-server -SkipRemoteProvision
```

Resultat:

- build Docker backend OK;
- container backend recree;
- `/ready => OK`.

### QA ingestion

Commande utilisee:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-ingestion-quality-checks.ps1 -SshTarget saaia-server -Category Cuisine -RecentHours 12 -StrictFailedJobs -SkipQdrantVectorCheck
```

Resultat:

- `ok: true`
- `issues: []`
- warnings restants non bloquants listes plus haut.

### Validation Cuisine retrieval

Cas cible apres correctifs:

```powershell
$line = Select-String -Path infra\.env.server-linux,infra\.env -Pattern '^SAAIA_BOOTSTRAP_API_KEY=' | Select-Object -First 1
$env:SAAIA_API_KEY = ($line.Line -split '=',2)[1].Trim().Trim('"')
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 -Mode retrieval -BackendBaseUrl "https://saaia-server.taila2196b.ts.net" -Category "Cuisine" -Ids "Q007,Q154" -Parallelism 1 -TimeoutSeconds 240
```

Resultat:

- `Q007`: 8 sources, `nobilia-recettes-internationales-FR.pdf` en top 1;
- `Q154`: 0 source, considere coherent car la question ne donne aucun sujet a chercher.

Batch Cuisine `Q001-Q025`:

```powershell
$line = Select-String -Path infra\.env.server-linux,infra\.env -Pattern '^SAAIA_BOOTSTRAP_API_KEY=' | Select-Object -First 1
$env:SAAIA_API_KEY = ($line.Line -split '=',2)[1].Trim().Trim('"')
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 -Mode retrieval -BackendBaseUrl "https://saaia-server.taila2196b.ts.net" -Category "Cuisine" -Offset 0 -Limit 25 -Parallelism 1 -TimeoutSeconds 90
```

Artefact:

- `artifacts/llm-validation/cuisine-v1-retrieval-20260515_110147_521.json`

Resultat:

- `25/25` avec sources;
- `0` erreur;
- `0` zero source;
- `15` target matched;
- `0` target missed;
- `14` top1 doc hit;
- `15` top3 doc hit.

## Problemes restants

### 1. Campagne complete en un seul bloc instable

La commande complete:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 -Mode retrieval -BackendBaseUrl "https://saaia-server.taila2196b.ts.net" -Category "Cuisine" -Parallelism 4 -TimeoutSeconds 240
```

a ete interrompue par timeout outil apres plusieurs heures. Elle n'a pas produit d'artefact final complet.

Artefact partiel:

- `artifacts/llm-validation/cuisine-v1-retrieval-20260514_201255_901.parts`

Constat:

- seulement 44 lignes JSONL ecrites;
- plusieurs erreurs `Exception calling "GetResult" ... An error occurred while sending the request`;
- pas de nouvel artefact final complet.

Interpretation:

- ne plus relancer les 294 questions en un seul bloc pour l'instant;
- preferer des lots bornes de 25 questions;
- `Parallelism 1` est fiable;
- `Parallelism 2` peut etre teste ensuite, mais prudemment;
- `Parallelism 4` est trop long/fragile pour le harness actuel, meme si le serveur expose 4 slots.

### 2. Q154 reste sans source

Question:

```text
Reponds seulement si tu trouves une source dans les PDF.
```

Resultat:

- HTTP 200;
- 0 source.

Ce comportement est acceptable car la question ne contient aucun sujet exploitable. Eventuellement, le client/agent pourrait transformer ce cas en demande de clarification utilisateur.

### 3. Tests LLM client UI reels pas encore fermes

La validation actuelle est surtout retrieval backend. Les tests de reponse LLM client de bout en bout restent a faire apres avoir termine les lots retrieval.

Point a verifier avant test agent/LLM:

```powershell
Invoke-RestMethod http://127.0.0.1:1234/v1/models -TimeoutSec 5
```

Lors du dernier controle, le LLM local n'etait pas joignable sur `127.0.0.1:1234`.

### 4. Ingestion/OCR encore ameliorable

La QA ingestion est verte, mais certains documents ont encore des warnings de pages pauvres ou OCR/review recommande.

Priorite future:

- ameliorer distinction contenu reel vs index/table des matieres;
- mieux exploiter OCR image dans les PDF mixtes;
- verifier les chunks pauvres;
- eventuellement renforcer les content cards et profils LLM serveur.

## Commandes utiles pour reprendre

### Recuperer la branche sur l'autre PC

```powershell
git clone https://github.com/maximebirchler/SAAIA-RAG.git
cd RAG
git checkout SAAIA_V3.1
git pull origin SAAIA_V3.1
```

Si le depot existe deja:

```powershell
cd C:\chemin\vers\RAG
git checkout SAAIA_V3.1
git pull origin SAAIA_V3.1
```

### Verifier le serveur

```powershell
$line = Select-String -Path infra\.env.server-linux,infra\.env -Pattern '^SAAIA_BOOTSTRAP_API_KEY=' | Select-Object -First 1
$key = ($line.Line -split '=',2)[1].Trim().Trim('"')
$ready = Invoke-RestMethod -Method Get -Uri 'https://saaia-server.taila2196b.ts.net/ready' -Headers @{ 'X-Admin-Key' = $key } -TimeoutSec 30
$ready | ConvertTo-Json -Depth 6
```

### Relancer les tests backend

```powershell
dotnet test backend\SAAIA.Backend.Tests\SAAIA.Backend.Tests.csproj --no-restore
```

### Continuer la campagne Cuisine par lots

Le premier lot `Offset 0 -Limit 25` est valide. Reprendre a `Offset 25`.

```powershell
$line = Select-String -Path infra\.env.server-linux,infra\.env -Pattern '^SAAIA_BOOTSTRAP_API_KEY=' | Select-Object -First 1
$env:SAAIA_API_KEY = ($line.Line -split '=',2)[1].Trim().Trim('"')
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 -Mode retrieval -BackendBaseUrl "https://saaia-server.taila2196b.ts.net" -Category "Cuisine" -Offset 25 -Limit 25 -Parallelism 1 -TimeoutSeconds 90
```

Puis continuer:

```powershell
# exemples
-Offset 50 -Limit 25
-Offset 75 -Limit 25
-Offset 100 -Limit 25
```

Eviter pour l'instant:

```powershell
-Parallelism 4
```

sur la campagne complete.

### Lire rapidement le resultat d'un lot

```powershell
$file = Get-ChildItem artifacts\llm-validation -Filter "cuisine-v1-retrieval-*.json" |
  Where-Object { $_.Name -notlike "*.success.json" } |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1

$j = Get-Content -Raw $file.FullName | ConvertFrom-Json
$j.totals | ConvertTo-Json -Compress
$j.rows |
  Where-Object { $_.error -or [int]$_.sourceCount -eq 0 -or $_.targetMatched -eq 'no' } |
  Select-Object id,theme,sourceCount,targetMatched,top1DocPath,error,elapsedMs,backendTookMs |
  Format-Table -Wrap -AutoSize
```

### Deployer le backend si nouveau correctif

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File infra\scripts\prod\deploy-remote-backend.ps1 -ContextName saaia-server -SkipRemoteProvision
```

### Envoyer une notification Telegram

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\MBirchler\Desktop\ecom\SAAIA\SAAIA - Notifier\notify-codex.ps1" -Title "Codex SAAIA" -Message "Message court de fin de tache"
```

## Erreurs rencontrees

### Dapper / DocumentProfileMatchRow

Erreur:

```text
System.InvalidOperationException:
A parameterless default constructor or one matching signature (...) is required for
SAAIA.Backend.Endpoints.RagEndpoints+DocumentProfileMatchRow materialization
```

Cause:

- mismatch `System.Array` vs `string[]?` sur les colonnes PostgreSQL `text[]`.

Statut:

- corrige par `8756b0eb`;
- redeploye.

### HTTP 500 / 408 pendant campagne retrieval

Symptomes:

- nombreux HTTP 500;
- un HTTP 408;
- erreurs PowerShell `An error occurred while sending the request`.

Statut:

- les HTTP 500 lies aux profils sont corriges;
- le harness complet reste trop fragile en gros bloc;
- utiliser des batches de 25.

### Category vs categoryPath

Symptome:

- `category=Cuisine` retournait 0 alors que `categoryPath=Cuisine` trouvait les sources.

Statut:

- le harness utilise maintenant `categoryPath`;
- backend runtime reste compatible avec les categories normalisees, mais pour les dossiers documents il faut privilegier `categoryPath`.

### Q007 zero source avec topK=8

Symptome:

- `Est-ce qu'il y a des recettes internationales dans les PDF ?` retournait 0 source a `topK=8`, mais trouvait `nobilia-recettes-internationales-FR.pdf` a `topK=10`.

Statut:

- corrige par detection generique d'intention d'existence/inventaire de corpus;
- Q007 valide avec 8 sources.

## Prochaines etapes recommandees

1. Continuer la campagne retrieval Cuisine a partir de `Offset 25`, par lots de 25, `Parallelism 1`.
2. Pour chaque lot, relever:
   - erreurs;
   - zero source;
   - target missed;
   - mauvais top1/top3.
3. Corriger uniquement des patterns generiques:
   - aucune regle basee sur un nom de PDF;
   - aucune regle basee sur un nom de recette;
   - aucune logique specifique Cuisine.
4. Quand tous les lots retrieval sont acceptables, lancer les tests LLM client/agent sur un sous-ensemble representatif.
5. Ensuite seulement tester dans l'UI reelle:
   - questions exactes;
   - questions larges;
   - questions ambigues;
   - multilingue;
   - cartes sources;
   - cas ou il faut demander une precision.
6. Reprendre ensuite le chantier ingestion/OCR:
   - pages pauvres;
   - index/table des matieres;
   - chunks OCR imparfaits;
   - content cards/profils LLM serveur;
   - resumes backoffice.

## Consignes importantes

- Le LLM serveur reste backoffice uniquement: ingestion, enrichissement, resumes, profils documentaires.
- Le chat utilisateur reste gere par le LLM local client.
- Le backend doit traiter toute langue de document.
- Le client UI reste limite aux 6 langues definies.
- Ne pas coder en dur de termes Cuisine, noms de recettes, noms de PDF ou contenu issu des documents indexes.
- Les ameliorations doivent etre generiques et reutilisables pour toutes les categories futures.
- Utiliser `categoryPath` pour scoper une categorie documentaire reelle.
- Ne pas relancer la campagne complete 294 questions en un seul bloc tant que le harness n'a pas ete durci.
- Toujours verifier `/ready` avant une grosse campagne.
- Toujours envoyer la notification Telegram en fin de longue session.
- Commit/push avant de changer de machine.
