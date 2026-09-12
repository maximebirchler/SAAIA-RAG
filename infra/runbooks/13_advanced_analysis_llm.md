# Runbook — grand modèle de la capacité avancée

Statut : **TESTE_NON_APPROUVE**

Ce runbook configure le fournisseur appelé par les jobs durables
`AdvancedAnalysis`. Le client de production reste en mode LLM local : c'est la
frontière locale qui décide de créer le handoff avancé.

## Profils

Définir `SAAIA_ADVANCED_ANALYSIS_PROVIDER` à l'une des valeurs suivantes :

- `disabled` : worker LLM désactivé ;
- `openai-dev` : GPT-5.6 Terra, baseline temporaire ;
- `runpod-bench` : endpoint OpenAI-compatible RunPod ;
- `customer-server` : endpoint OpenAI-compatible sur le réseau du client.

La configuration signée contient aussi `LlmLocation`. L'installateur la génère
à partir de `SAAIA_ADVANCED_LLM_LOCATION` : `external-service` pour OpenAI et
RunPod, `internal` pour le serveur du client. Les profils externes exigent une
URL HTTPS. Le profil client peut utiliser une URL HTTP privée selon la politique
réseau du déploiement. Une incohérence entre profil et localisation est rejetée
avant tout appel HTTP.

## Variables prises en charge dans le lot actuel

```text
SAAIA_ADVANCED_ANALYSIS_ENABLED
SAAIA_ADVANCED_ANALYSIS_PROVIDER
SAAIA_ADVANCED_LLM_LOCATION
SAAIA_ADVANCED_LLM_BASE_URL
SAAIA_ADVANCED_LLM_MODEL
SAAIA_ADVANCED_LLM_API_KEY
```

Le secret est injecté dans le conteneur backend et référencé par
`ENV:SAAIA_ADVANCED_LLM_API_KEY` dans la configuration signée. Il ne doit pas
être écrit dans le JSON versionné, un argument de commande, un artefact de test
ou Git.

Les limites de tokens, de coût, de délai et de recherche possèdent pour
l'instant des valeurs sûres dans le template signé. Le futur assistant
d'installation pourra les exposer sous contrôle de la licence ; ce lot ne crée
pas de variables d'environnement qui ne seraient pas encore lues par les
scripts.

## Rétention des transferts avancés

Chaque job reçoit une échéance calculée à sa création à partir de
`RetentionDays`, borné entre 1 et 365 jours. Un worker de rétention distinct du
worker LLM balaie la table toutes les cinq minutes par défaut. Il supprime par
lots les handoffs, résultats et métadonnées de job arrivés à échéance ; les
traces d'outils associées sont supprimées par la contrainte PostgreSQL
`ON DELETE CASCADE`.

Ce worker reste actif même si la licence avancée ou le fournisseur LLM est
ensuite désactivé, afin qu'un changement de licence ne suspende pas la politique
de suppression. Un job en cours conserve ses données tant que son bail est
encore actif. Dès que l'échéance de rétention est atteinte, son bail ne peut plus
être renouvelé ; la suppression devient possible après la fin du bail. Les
valeurs signées par défaut sont `RetentionSweepMilliseconds=300000` et
`RetentionDeleteBatchSize=1000`.

## Baseline OpenAI Terra

Valeurs de référence :

```text
SAAIA_ADVANCED_ANALYSIS_ENABLED=true
SAAIA_ADVANCED_ANALYSIS_PROVIDER=openai-dev
SAAIA_ADVANCED_LLM_LOCATION=external-service
SAAIA_ADVANCED_LLM_BASE_URL=https://api.openai.com/v1
SAAIA_ADVANCED_LLM_MODEL=gpt-5.6-terra
SAAIA_ADVANCED_LLM_API_KEY=<secret injecté hors Git>
```

La configuration générée active explicitement l'autorisation de contenu et de
métadonnées externes. Vérifier le journal global avant tout appel et conserver
les limites 25/20/24 USD tant qu'une nouvelle autorisation n'a pas été donnée.

Sonde synthétique sans corpus privé :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test-advanced-server-provider.ps1 -Provider OpenAI
```

Parcours produit du cas gelé avec backend temporaire, retrieval réel et petit
modèle local :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test-advanced-product-path-openai.ps1 `
  -ServerEnvPath <chemin-vers-.env.server-linux> `
  -OpenAiModel gpt-5.6-terra `
  -Ids A755-ADV-01-meal-grid-5x4 `
  -Repetitions 1
```

## Benchmark RunPod

Le candidat, l'endpoint et les tarifs sont choisis et revérifiés au moment du
benchmark. Le lanceur ne fournit aucune valeur RunPod implicite : cela évite de
réutiliser par erreur un modèle, un endpoint ou un prix devenu obsolète. Les
familles Qwen autour de 32B restent des candidates, sans figer ici le format ou
la quantification.

Importer la clé RunPod depuis le presse-papiers dans DPAPI :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\import-llm-secret-from-clipboard.ps1 -Provider RunPod
```

Le budget RunPod n'est jamais déduit de l'autorisation OpenAI. Il doit être
fourni explicitement et possède son propre registre persistant. L'enveloppe et
les prix doivent venir d'une autorisation dédiée et d'une vérification du tarif
du candidat réellement sélectionné.

Sonde synthétique sans corpus privé :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test-advanced-server-provider.ps1 `
  -Provider RunPod `
  -BaseUrl <endpoint-openai-compatible> `
  -ModelId <modele-exact> `
  -ProviderRuntime <runtime-servi> `
  -AuthorizedBudgetUsd <budget-autorise> `
  -InputUsdPerMillionTokens <tarif-entree> `
  -CachedInputUsdPerMillionTokens <tarif-entree-cachee> `
  -OutputUsdPerMillionTokens <tarif-sortie> `
  -RuntimeProfile <profil-reproductible> `
  -Gpu <gpu-observe> `
  -Quantization <quantification> `
  -ModelSha256 <hash-si-disponible>
```

Parcours produit complet :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test-advanced-product-path-runpod.ps1 `
  -ServerEnvPath <chemin-vers-.env.server-linux> `
  -BaseUrl <endpoint-openai-compatible> `
  -ModelId <modele-exact> `
  -ProviderRuntime <runtime-servi> `
  -AuthorizedBudgetUsd <budget-autorise> `
  -InputUsdPerMillionTokens <tarif-entree> `
  -CachedInputUsdPerMillionTokens <tarif-entree-cachee> `
  -OutputUsdPerMillionTokens <tarif-sortie> `
  -RuntimeProfile <profil-reproductible> `
  -Gpu <gpu-observe> `
  -Quantization <quantification> `
  -ModelSha256 <hash-si-disponible> `
  -ContextSize <contexte> `
  -HourlyCostUsd <cout-horaire-si-applicable> `
  -Repetitions 3
```

Le lanceur RunPod utilise les mêmes tools, la même banque, le même corpus et les
mêmes critères sémantiques que Terra. Il refuse de démarrer si le budget ou les
tarifs ne sont pas définis.

Consigner l'image serveur, le hash du modèle, la quantification, le GPU, la
fenêtre de contexte et tous les paramètres runtime. Arrêter le pod à la fin de
la campagne et joindre le coût réel à l'artefact.

## Serveur on-prem du client

Renseigner le profil `customer-server` dans l'installation. Le fichier
`infra/docker-compose.advanced-llm.yml` propose le service llama-server. Il
constitue un projet Compose séparé afin de pouvoir démarrer, arrêter ou remplacer
le grand modèle sans recréer PostgreSQL, Qdrant ou le backend. La stack SAAIA
principale doit être démarrée en premier : elle crée le réseau externe désigné
par `SAAIA_LLM_DOCKER_NETWORK`, `infra_default` dans l'installation Linux
standard.

Ne pas fusionner les deux fichiers avec plusieurs options `-f`. Le fichier du
grand modèle contient son propre `name:` ; lors d'une fusion, Docker Compose
emploierait le dernier nom de projet et pourrait placer les services de la stack
principale sur un autre réseau par défaut. Valider puis démarrer uniquement le
projet avancé :

```powershell
docker network inspect infra_default
docker compose --env-file .\infra\.env.server-linux `
  -f .\infra\docker-compose.advanced-llm.yml config --quiet
docker compose --env-file .\infra\.env.server-linux `
  -f .\infra\docker-compose.advanced-llm.yml up -d
```

Adapter `infra_default` si `SAAIA_LLM_DOCKER_NETWORK` porte une autre valeur.
Le backend utilise l'adresse privée Docker `http://advanced-llm:8080`. Après
démarrage, contrôler `/health` et `/v1/models`, puis exécuter le même parcours
produit avec `-ExpectedAdvancedProvider customer-server` et l'identifiant exact
du modèle.

## Contrôles de fin de campagne

- le fournisseur et le modèle observés correspondent à la configuration ;
- les appels passent uniquement par le job durable et les tools SAAIA ;
- les claims ne citent que des preuves canoniques revalidées ;
- les métriques et le coût figurent dans l'artefact sans contenu ni secret ;
- une erreur reste typée, sans fallback silencieux ;
- les processus et ressources payantes appartenant à la campagne sont arrêtés ;
- le verdict sémantique reste distinct du résultat mécanique du test.
