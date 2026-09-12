# Candidats RunPod pour la validation A763

Date de recherche : 12 septembre 2026

Statut : **PROPOSITION SANS AUTORISATION DE DEPENSE**

Statut produit : **TESTE_NON_APPROUVE**

Ce document prépare la validation du même parcours produit que Terra : petit
modèle local, handoff, job durable sur le backend, tools RAG SAAIA, grand modèle,
validation des claims et cartes source. RunPod remplace seulement le fournisseur
du grand modèle. Aucun compte, crédit, endpoint, pod, clé ou appel RunPod n'a été
créé pendant cette recherche.

## Candidat R1 recommandé — endpoint public Qwen3 32B AWQ

La première campagne doit employer l'endpoint public au token. Il n'impose ni
déploiement de conteneur, ni location horaire d'un GPU, ni coût d'inactivité. La
fiche officielle déclare un format OpenAI compatible, le modèle et le tarif
suivants :

| Paramètre scellé | Valeur proposée |
|---|---|
| Provider SAAIA | `runpod-bench` |
| Localisation | `external-service` |
| Base URL OpenAI compatible | `https://api.runpod.ai/v2/qwen3-32b-awq/openai/v1` |
| ModelId demandé | `Qwen/Qwen3-32B-AWQ` |
| Runtime | `runpod-public-openai` |
| Profil | `qwen3-32b-awq-public-20260720` |
| Quantification | `AWQ 4-bit` |
| Contexte déclaré par le modèle | `32768` tokens natifs |
| Prix entrée | `10 USD/M tokens` |
| Prix entrée en cache | `10 USD/M tokens` |
| Prix sortie | `10 USD/M tokens` |
| GPU et coût horaire | non exposés par l'endpoint public |
| SHA des poids | non exposé par l'endpoint public |

Sources consultées :

- [fiche RunPod Qwen3 32B AWQ](https://docs.runpod.io/public-endpoints/models/qwen3-32b) ;
- [catalogue des endpoints publics RunPod](https://docs.runpod.io/public-endpoints/reference) ;
- [carte officielle Qwen3-32B-AWQ](https://huggingface.co/Qwen/Qwen3-32B-AWQ).

La carte Qwen décrit 32,8 milliards de paramètres, une quantification AWQ 4
bits, 32 768 tokens de contexte natif et une licence Apache-2.0. YaRN ne doit pas
être activé pour cette première campagne : Qwen indique qu'il peut dégrader les
contextes courts et SAAIA borne déjà les preuves bien en dessous du cas 131k.

### Enveloppe de coût proposée

RunPod publie un prix plat de 10 USD par million de tokens. Le garde-fou SAAIA
doit donc recevoir 10 pour l'entrée, l'entrée en cache et la sortie. Cette
configuration évite de supposer une remise de cache que RunPod ne publie pas.

| Volume total | Coût maximal au tarif publié |
|---:|---:|
| 100 000 tokens | 1,00 USD |
| 192 000 tokens, soit 24 appels de 7k entrée + 1k sortie | 1,92 USD |
| 300 000 tokens | 3,00 USD |
| 384 000 tokens, soit 48 appels de 7k entrée + 1k sortie | 3,84 USD |

Pour les quatre cas répétés trois fois, l'autorisation proposée est limitée à
**5 USD** : alerte à 4 USD, arrêt local à 4,80 USD, maximum 0,40 USD par job et
quatre appels par job. Le plafond par job couvre Planner, Writer et au plus une
réparation, tout en arrêtant une dérive avant qu'elle affecte le reste de la
banque. La dépense réelle devra être rapprochée du registre SAAIA et du tableau
RunPod après chaque jalon.

Invocation préparée, à ne lancer qu'après autorisation explicite et import de la
clé dans le coffre SAAIA :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test-advanced-product-path-runpod.ps1 `
  -ServerEnvPath <server-env-path> `
  -AuthorizedBudgetUsd 5 `
  -SoftLimitUsd 4 `
  -HardLimitUsd 4.80 `
  -MaximumCostPerJobUsd 0.40 `
  -MaximumCallsPerJob 4 `
  -Repetitions 3 `
  -DelayBetweenCasesSeconds 2 `
  -BaseUrl "https://api.runpod.ai/v2/qwen3-32b-awq/openai/v1" `
  -ModelId "Qwen/Qwen3-32B-AWQ" `
  -InputUsdPerMillionTokens 10 `
  -CachedInputUsdPerMillionTokens 10 `
  -OutputUsdPerMillionTokens 10 `
  -ProviderRuntime "runpod-public-openai" `
  -RuntimeProfile "qwen3-32b-awq-public-20260720" `
  -Quantization "AWQ 4-bit" `
  -ContextSize 32768
```

## Limite de preuve du candidat R1

L'endpoint public suffit pour savoir si un Qwen open source plus grand peut
faire fonctionner le parcours SAAIA et produire des réponses acceptables. Il ne
suffit pas à reproduire exactement l'infrastructure : RunPod ne publie pas dans
cette fiche le hash des poids servis, la révision du conteneur, le GPU ou les
paramètres de runtime. Le preflight pourra sceller l'URL, le ModelId demandé, la
date de la fiche et toutes les métriques renvoyées, mais il ne devra pas inventer
un hash ou un GPU.

Ce candidat est donc une preuve fonctionnelle et économique. La preuve de
reproductibilité matérielle doit ensuite employer un endpoint Serverless privé
ou le serveur du client, avec image, modèle, révision, hash, GPU, contexte et
paramètres vLLM scellés.

## Candidat R2 conditionnel — Serverless privé Qwen3 30B A3B Instruct 2507

Ce candidat ne sera ouvert que si R1 échoue pour une cause sémantique attribuée
au modèle. `Qwen/Qwen3-30B-A3B-Instruct-2507` est un modèle MoE de 30,5
milliards de paramètres, dont 3,3 milliards activés, non-thinking et avec un
contexte natif de 262 144 tokens. Pour SAAIA, le premier profil devra limiter
`MAX_MODEL_LEN` à 32 768 afin de réduire la VRAM et conserver une comparaison
cohérente avec R1. La révision exacte des poids, l'image vLLM et le GPU resteront
à sélectionner puis à mesurer avant toute dépense.

Sources :

- [carte officielle Qwen3-30B-A3B-Instruct-2507](https://huggingface.co/Qwen/Qwen3-30B-A3B-Instruct-2507) ;
- [configuration vLLM RunPod](https://docs.runpod.io/serverless/vllm/configuration) ;
- [variables vLLM RunPod](https://docs.runpod.io/serverless/vllm/environment-variables) ;
- [facturation Serverless RunPod](https://docs.runpod.io/serverless/pricing).

RunPod facture Serverless à la seconde depuis le démarrage complet du worker
jusqu'à son arrêt. Le chargement du modèle, l'exécution, le délai d'inactivité
et le stockage participent au coût. Il faut donc une autorisation distincte,
un arrêt vérifié et un rapprochement de facture. Aucun tarif GPU horaire n'est
figé ici, car la page de facturation consultée renvoie au catalogue de prix
RunPod et le matériel n'a pas encore été choisi.

## Ordre et critères d'acceptation

Après autorisation de dépense, la campagne R1 suivra cet ordre :

1. créer ou utiliser un compte RunPod crédité et importer une clé limitée dans
   le coffre local SAAIA ;
2. exécuter une sonde OpenAI compatible minimale, vérifier l'authentification,
   le ModelId demandé, le JSON structuré, l'usage et le coût ;
3. lancer une seule fois le planning 5 x 4 par le parcours produit complet ;
4. relire chaque cellule et chaque preuve avant d'autoriser la suite ;
5. si ce résultat est acceptable, exécuter les quatre cas trois fois sur le
   même commit, la même banque, le même profil et le même corpus ;
6. produire l'assessment mécanique, puis une revue sémantique séparée ;
7. vérifier le registre de coût, l'arrêt des processus SAAIA temporaires et
   l'absence de ressource RunPod privée résiduelle.

Les critères sont ceux de Terra : douze jobs terminaux attendus, trois
plannings 5 x 4 acceptés, chaque relation cellule/preuve soutenue, aucune source
forgée, mêmes fournisseur et modèle pour Planner/Writer/réparation, citations
ouvrables, latence et coût consignés. Un test mécanique vert ne vaut pas
approbation sémantique.

## Protection des données et décision requise

Le test transmettra à un service RunPod externe la demande, les prompts et les
extraits de preuves sélectionnés. PostgreSQL, Qdrant, les chemins et les secrets
resteront dans SAAIA. Avant le premier appel, l'utilisateur devra autoriser à la
fois cette sortie de contenu et un budget RunPod maximal de 5 USD. Jusqu'à cette
décision, la configuration reste inactive et aucune clé RunPod n'est requise.

R1 est le candidat recommandé parce qu'il donne la réponse produit recherchée
avec un coût au token borné. R2 reste une voie de diagnostic si la qualité de R1
est insuffisante ou si une preuve d'infrastructure reproductible est nécessaire.
