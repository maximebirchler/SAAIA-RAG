# Vision licence et installation des capacités LLM

Date de référence : 11 septembre 2026
Statut : **VISION CIBLE — IMPLÉMENTATION HORS DU LOT ACTUEL**

Ce document fixe les contraintes que l'architecture actuelle doit respecter. Il
ne décrit pas encore un format de licence public ni un assistant d'installation
livrable.

## Séparer droit, topologie et profil technique

La licence doit exprimer ce que le client a acheté. L'installation choisit où
et comment ces capacités s'exécutent. Le profil technique contient les détails
modifiables de connexion et de runtime.

```text
Licence signée
  -> capacités autorisées
       local simple
       grand modèle
       mode hybride local + grand modèle
       fournisseurs externes autorisés ou interdits

Installation
  -> topologie retenue pour ce client
       modèle sur le poste
       modèle sur serveur on-prem
       endpoint cloud géré par le client
       API d'un fournisseur

Profil technique
  -> modèle, URL, secretRef, contexte, quantification, GPU et limites
```

Une licence ne doit contenir ni clé API, ni mot de passe, ni URL sensible. Un
changement d'URL ou de modèle ne doit pas exiger une modification du code RAG.
Une configuration d'installation ne doit jamais permettre une capacité que la
licence signée n'autorise pas.

## Combinaisons à prévoir

| Capacité achetée | Comportement attendu |
|---|---|
| local seul | les demandes dans la frontière locale sont traitées ; les demandes avancées reçoivent un état explicite indisponible |
| grand modèle seul | toutes les demandes suivent une orchestration serveur ; ce point d'entrée général reste à concevoir |
| hybride | le petit modèle traite les cas qualifiés et transfère le reste au job serveur |
| hybride avec API externe | même routage, provider serveur configuré vers l'API autorisée |
| hybride on-prem | même routage, provider serveur configuré vers le grand modèle interne |

Le lot actuel construit et valide le troisième parcours, avec Terra puis RunPod
comme étapes temporaires avant le serveur client. Il ne faut pas prétendre que
le mode « grand modèle seul » est déjà livré : le contrat actuel part d'un
handoff produit par la frontière locale.

## Installateur backend

L'installateur devra :

1. vérifier la licence et n'afficher que les topologies autorisées ;
2. demander le type de fournisseur et les politiques de sortie de données ;
3. collecter l'URL et importer le secret dans un coffre sans le journaliser ;
4. contrôler HTTPS pour les services externes et la joignabilité de l'endpoint ;
5. interroger `/models` et figer l'identité réellement servie ;
6. évaluer CPU, RAM, VRAM, stockage et concurrence pour une installation
   on-prem ;
7. télécharger une image et un modèle approuvés avec hash et licence du modèle ;
8. générer et signer la configuration SAAIA ;
9. exécuter une sonde synthétique puis présenter un diagnostic exploitable ;
10. permettre une reconfiguration sans migration de la logique RAG.

Les scripts existants `infra/scripts/llm/install-llm.ps1` et
`sync-llm-capacity-plan.ps1` constituent des briques pour le dimensionnement du
serveur ; ils ne sont pas encore l'assistant de licence décrit ici.

## Installateur client

L'installateur devra :

1. vérifier si la capacité locale est incluse et choisie ;
2. mesurer CPU, RAM, VRAM, espace disque et compatibilité du runtime ;
3. proposer seulement des modèles et quantifications compatibles avec le
   matériel et la licence commerciale du modèle ;
4. télécharger modèle et dépendances, vérifier leurs hashes, puis exécuter le
   warmup et la sonde structurée ;
5. configurer le mode local, serveur ou hybride sans exposer de clé fournisseur
   dans le client ;
6. enregistrer un profil de performance et une capacité de concurrence
   cohérente avec le nombre de sièges.

Dans un mode serveur ou hybride, les secrets de fournisseurs externes doivent
rester sur le backend. Les modes directs `OpenAiDev` et `RunPodBench` du client
actuel servent au développement et au benchmark ; ils ne définissent pas le
stockage des secrets du produit final.

## Contraintes imposées au code actuel

- le routage métier dépend d'une capacité, jamais d'une marque d'hébergeur ;
- `IAdvancedAnalysisProvider` reste l'unique frontière du grand modèle ;
- les tools, preuves et résultats restent des contrats SAAIA indépendants du
  fournisseur ;
- les profils `openai-dev`, `runpod-bench` et `customer-server` sont
  remplaçables sans changer le client ;
- la configuration signée déclare séparément `Provider` et `LlmLocation`, et
  le backend rejette une combinaison incohérente avant toute sortie réseau ;
- l'entitlement `AdvancedAnalysisEnabled` actuel reste un premier verrou et ne
  préjuge pas du futur schéma complet de capacités ;
- les futures migrations de licence devront être versionnées et compatibles
  avec les installations existantes ;
- toute topologie doit exposer la même télémétrie, les mêmes erreurs typées et
  les mêmes critères de validation sémantique.

## Décisions à prendre après les validations du lot

Restent à décider avec des preuves de coût, qualité et exploitation : le format
signé exact des capacités, les combinaisons commerciales, la gestion du mode
grand modèle seul, la liste des fournisseurs supportés, Azure ou d'autres clouds,
le stockage multiplateforme des secrets, le catalogue de modèles et le parcours
UX des deux installateurs.
