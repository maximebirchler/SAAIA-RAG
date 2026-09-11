# Audit du Goal actif — frontière locale et capacité avancée A755–A763

Date : 11 septembre 2026
Branche : `SAAIA_V3.1`
Statut produit : **TESTE_NON_APPROUVE**

## Verdict actuel

La frontière du petit modèle local est qualifiée et reproductible sur la banque
adversariale connue A755. L'architecture hybride locale-vers-serveur est
implémentée depuis la décision locale jusqu'au résultat avancé durable dans le
client. Les contrats, la persistance, le worker, les tools, la reprise WinUI, le
fournisseur OpenAI-compatible, les garde-fous de coût et les profils de
déploiement passent les validations mécaniques.

La capacité avancée n'est pas encore validée sémantiquement avec un grand modèle
réel. Aucun appel payant OpenAI, RunPod ou serveur client n'a été exécuté. Le
planning de cinq jours par quatre repas n'a donc pas encore obtenu ses trois
réussites live consécutives. Le Goal reste actif et le produit ne peut pas être
approuvé.

## Reprise documentaire et état du dépôt

Le registre de lecture rapporte 43 documents sur 43 terminés, soit
15 847 677 caractères sur 15 847 677. Le dépôt de travail est la reprise du
laptop sur `SAAIA_V3.1`; le transfert initial ne doit pas être rejoué.

Les lots d'architecture précédents ont été commités et poussés :

- `62b4215e` rend la reprise des jobs avancés durable dans WinUI ;
- `b30096ac` introduit le contrat de fournisseur LLM et les harnais Terra et
  RunPod ;
- `9101e5f5` relie le parcours produit au fournisseur de grand modèle exécuté
  par le backend.

## Ce qui est validé

### A755 — frontière du petit modèle local

La configuration qualifiée est la machine cliente actuelle avec RTX 4060,
Qwen3-4B Instruct Q5_K_M, llama.cpp CUDA b10098 et 4 096 tokens de contexte. La
banque finale comporte quatorze cas répétés trois fois : 42 résultats sur 42
sont acceptés sémantiquement, quatorze routes sur quatorze sont stables, sans
fait non soutenu ni substitution de source, et toutes les portes de latence sont
respectées.

La frontière publiée comporte quatre issues :

- `local_source_backed_answer` pour les demandes directes dans l'enveloppe
  mesurée et l'extraction bornée qualifiée ;
- `clarification` quand une identité documentaire indispensable manque ;
- `insufficient_evidence` quand le corpus ne contient réellement pas le
  document demandé ;
- `advanced_analysis_required` pour les comparaisons, les structures et les
  collections au-delà de l'enveloppe, ou après épuisement du budget local.

Le planning 5 × 4 est correctement classé hors de la capacité locale. Le succès
du petit modèle consiste ici à transférer la demande, pas à improviser une
réponse incomplète.

### A756–A762 — chemin avancé durable

Les propriétés suivantes sont couvertes :

- handoff typé conservant tenant, utilisateur, projet, session, intention,
  charge observée et références documentaires ;
- création idempotente d'un job PostgreSQL durable soumise à l'entitlement de
  licence ;
- worker avec lease, heartbeat, retry, annulation et revalidation des preuves ;
- accès au corpus uniquement par des tools SAAIA bornés et isolés par tenant ;
- trace durable sans corps de preuve ni secret ;
- transport client create/get/cancel et validation stricte du résultat, des
  citations et des cartes source ;
- première persistance avant l'attente de polling, puis reprise du même job
  après redémarrage de WinUI sans second `POST` ;
- fermeture de WinUI limitée à l'arrêt du tracker local, sans annulation du job
  serveur.

Le sous-ensemble ciblé de reprise A762 passe à 4/4. Un essai visuel fermant le
vrai processus WinUI pendant un vrai job long reste requis.

### Fournisseur du grand modèle

`IAdvancedAnalysisProvider` est l'unique frontière backend. Les profils
`openai-dev`, `runpod-bench` et `customer-server` partagent le même cycle
planner -> tools SAAIA -> writer, le même EvidenceBundle, le même validateur de
citations et les mêmes métriques. Une erreur fournisseur reste typée et ne
provoque aucun basculement silencieux.

Le budget Terra est persistant et conservateur : 25 USD autorisés, alerte à
20 USD, arrêt local à 24 USD, 0,50 USD maximum par job et quatre appels maximum
par job. Le journal contient les métriques d'usage et jamais les prompts, les
preuves ou les secrets.

La configuration signée sépare désormais le profil technique `Provider` de la
topologie `LlmLocation`. OpenAI et RunPod exigent `external-service`; le serveur
du client exige `internal`. Les scripts d'installation produisent cette valeur,
les droits de transmission externe en dépendent, et le backend rejette une
combinaison incohérente avant tout appel HTTP. Cette séparation permet au futur
catalogue de licence et aux installateurs de choisir les capacités et
l'hébergement sans modifier la logique RAG.

## Vérifications effectuées sur l'état courant

- tests ciblés du fournisseur : 11/11 ;
- tests ciblés de reprise A762 : 4/4 ;
- suite complète : 4 359 réussis, 2 sondes live explicitement ignorées,
  0 échec ;
- build de la solution : 0 avertissement, 0 erreur ;
- syntaxe de `_common.ps1` : valide ;
- syntaxe de `install.sh` avec Git Bash : valide ;
- template de production : 36 placeholders sur 36 pris en charge par les deux
  installateurs et JSON rendu syntaxiquement valide ;
- configurations signées réellement générées : 3/3 (`openai-dev`,
  `runpod-bench`, `customer-server`), avec localisation et politiques externes
  attendues ; incohérence profil/localisation rejetée ;
- aucun appel à un fournisseur externe et coût externe nul dans cette passe.

## Ce qui n'est pas validé

La banque A755 était connue pendant les corrections. Elle prouve une régression
stable, pas une généralisation aveugle. L'ancien holdout est contaminé et un
nouveau holdout ne doit être ouvert qu'après gel de l'état candidat final.

Les preuves suivantes manquent encore :

1. création réelle et appel synthétique de la clé Terra sans donnée privée ;
2. exécution du parcours produit local -> backend -> Terra sur le planning
   5 × 4 ;
3. inspection sémantique puis trois réussites consécutives sur état gelé ;
4. banque avancée multisource et mesure réelle des coûts, tokens et latences ;
5. comparaison du même protocole sur un modèle open source RunPod ;
6. sélection puis validation du modèle final sur serveur client ;
7. test WinUI réel avec ouverture exacte des cartes source et redémarrage en
   cours de job ;
8. nouveau holdout aveugle de bout en bout.

Docker et WSL ne sont pas disponibles sur cette machine, ce qui empêche ici la
validation sémantique de la composition des conteneurs. Git Bash valide la
syntaxe Linux. L'accès SSH sans mot de passe au serveur `saaia-server` est
refusé, donc aucun déploiement serveur n'a été tenté. Ces limites n'empêchent
pas la première validation Terra par API.

## Rapport avec la vision licence et installation

L'installateur commercial complet reste volontairement hors de ce lot. La
vision documentée sépare :

- les droits signés de licence : local, avancé, hybride et sortie externe ;
- la topologie choisie : poste client, serveur on-prem, cloud géré ou API ;
- le profil technique : URL, secret, modèle, contexte, quantification et
  capacité matérielle.

Le code actuel implémente le parcours hybride demandé. Le mode grand modèle seul
nécessitera plus tard un point d'entrée serveur général, car le trajet actuel
commence par un handoff de la frontière locale. Les installateurs backend et
client devront contrôler licence et matériel, collecter les secrets hors Git,
télécharger les modèles qualifiés, vérifier les hashes, générer la configuration
signée et exécuter des sondes de diagnostic.

## Séquence de validation restante

La prochaine action irréductible est la création des moyens Terra. L'interface
OpenAI montre actuellement zéro crédit et aucun moyen de paiement saisi. Le
formulaire de clé restreinte est préparé mais n'a pas été validé. Ces deux
actions nécessitent une confirmation immédiatement avant l'achat et la création
de la clé.

Après cette confirmation, la séquence prévue est :

1. acheter 25 USD de crédits et créer la clé restreinte temporaire ;
2. importer la clé dans le stockage secret sans la placer dans Git ou les logs ;
3. lancer une sonde synthétique sans corpus privé et vérifier identité, usage et
   coupe-circuits ;
4. activer le profil `openai-dev` dans le backend et exécuter une seule fois
   `A755-ADV-01-meal-grid-5x4` par le parcours produit ;
5. inspecter le résultat avant toute répétition payante ;
6. si le résultat est acceptable, exécuter les trois répétitions gelées puis la
   banque avancée ;
7. reproduire le protocole sur RunPod, sélectionner le candidat open source et
   le valider sur l'infrastructure finale du client ;
8. geler le code, ouvrir le nouveau holdout et terminer les preuves WinUI.

Le budget n'est donc pas consommé en lançant aveuglément mille questions. Chaque
palier commence par la sonde ou le cas minimal qui peut invalider la suite, puis
les répétitions coûteuses ne sont autorisées que si ce palier est accepté.
