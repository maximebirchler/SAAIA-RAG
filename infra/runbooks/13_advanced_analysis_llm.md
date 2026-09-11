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

Parcours produit du cas gelé :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test-advanced-analysis-agent-bank.ps1 `
  -ExpectedAdvancedProvider openai-dev `
  -ExpectedAdvancedModel gpt-5.6-terra `
  -Ids A755-ADV-01-meal-grid-5x4 `
  -Repetitions 1
```

## Benchmark RunPod

Configurer l'URL, le modèle et le secret du pod, puis employer les mêmes tools,
banque, corpus et critères sémantiques que Terra. La sonde synthétique utilise
`-Provider RunPod`. Le parcours produit attend
`-ExpectedAdvancedProvider runpod-bench`.

Consigner l'image serveur, le hash du modèle, la quantification, le GPU, la
fenêtre de contexte et tous les paramètres runtime. Arrêter le pod à la fin de
la campagne et joindre le coût réel à l'artefact.

## Serveur on-prem du client

Renseigner le profil `customer-server` dans l'installation. Le fichier
`infra/docker-compose.advanced-llm.yml` propose le service llama-server :

```powershell
docker compose -f .\infra\docker-compose.prod.yml `
  -f .\infra\docker-compose.advanced-llm.yml config
```

Le backend doit utiliser l'adresse privée Docker du service. Après démarrage,
contrôler `/health` et `/v1/models`, puis exécuter le même parcours produit avec
`-ExpectedAdvancedProvider customer-server` et l'identifiant exact du modèle.

## Contrôles de fin de campagne

- le fournisseur et le modèle observés correspondent à la configuration ;
- les appels passent uniquement par le job durable et les tools SAAIA ;
- les claims ne citent que des preuves canoniques revalidées ;
- les métriques et le coût figurent dans l'artefact sans contenu ni secret ;
- une erreur reste typée, sans fallback silencieux ;
- les processus et ressources payantes appartenant à la campagne sont arrêtés ;
- le verdict sémantique reste distinct du résultat mécanique du test.
