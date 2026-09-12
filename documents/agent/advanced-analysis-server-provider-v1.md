# Architecture du grand modèle serveur — capacité avancée A755

Date de référence : 12 septembre 2026
Statut produit : **TESTE_NON_APPROUVE**

## Décision d'architecture

Le parcours produit garde le petit modèle sur le poste du client. Ce modèle
traite les demandes situées dans sa frontière qualifiée et déclenche le handoff
`advanced_analysis_required` lorsqu'une demande dépasse cette frontière. Le
client crée alors un job durable sur le backend SAAIA. Le worker du backend
pilote la recherche dans le corpus, revalide les preuves et appelle le grand
modèle configuré.

```text
Question
  -> petit modèle local / frontière A755
       -> réponse locale, clarification ou insuffisance
       -> capacité avancée requise
            -> handoff signé et job durable SAAIA
            -> planification par le grand modèle
            -> recherches exécutées par les tools SAAIA
            -> preuves revalidées et bornées
            -> rédaction structurée par le même grand modèle
            -> validation des citations par SAAIA
            -> réponse et cartes source dans le client
```

Le choix de l'hébergement ne change pas ce flux. Un seul contrat
`IAdvancedAnalysisProvider` accepte aujourd'hui trois profils :

| Profil | Usage | Localisation déclarée |
|---|---|---|
| `openai-dev` | baseline temporaire avec GPT-5.6 Terra | service externe |
| `runpod-bench` | benchmark d'un modèle open source | service externe |
| `customer-server` | cible finale du produit | réseau interne du client |

Changer de profil modifie l'endpoint, le modèle, le secret et les paramètres de
génération. La frontière locale, le handoff, le stockage du job, les tools, le
format des preuves, le validateur de résultat et le client ne changent pas. Le
choix d'offrir d'autres hébergeurs pourra donc être traité dans l'installation
et le catalogue de licence après les validations Terra et RunPod.

La vision complète de licence et d'installation, y compris local seul, grand
modèle seul et hybride, est consignée dans
`llm-license-installation-vision.md`. Le présent lot implémente le parcours
hybride ; le mode grand modèle seul demandera plus tard un point d'entrée serveur
général qui ne dépend pas d'un handoff produit par le modèle local.

## Propriété des données et des tools

Le grand modèle ne dispose d'aucun accès direct à PostgreSQL, Qdrant, aux
fichiers ou à l'administration SAAIA. Il propose un plan de recherches JSON ;
le backend exécute ces recherches avec `IAdvancedAnalysisToolGateway` et dans
le tenant du job. Seuls des identifiants de preuve opaques et le contenu borné
des extraits revalidés sont transmis au rédacteur. Les chemins, identifiants de
documents, révisions et métadonnées canoniques restent côté SAAIA et sont
réinjectés dans le résultat après validation.

Le worker rejette les citations inconnues, les preuves forgées, les réponses
sans claim et les résultats hors contrat. Une erreur de fournisseur reste une
erreur typée du job ; aucun basculement silencieux vers un autre fournisseur
n'est effectué.

Le champ signé `LlmLocation` sépare explicitement la topologie du nom du profil.
Il vaut `external-service` pour OpenAI et RunPod, et `internal` pour le serveur
du client. Le backend rejette les valeurs inconnues et les combinaisons
profil/localisation incohérentes avant tout appel HTTP. Les profils externes
exigent HTTPS et les deux autorisations signées
`AllowExternalProviderContent` et `AllowExternalProviderMetadata`. Le profil
`customer-server` est déclaré interne et conserve ces autorisations à `false`.
La configuration de production générée rend donc l'envoi externe explicite et
auditable.

## Budget OpenAI temporaire

Le backend partage le journal de consommation Terra avec les sondes du client.
Avant chaque appel, il réserve un coût maximal selon la taille estimée de
l'entrée et le plafond de sortie. Les valeurs par défaut sont :

- budget autorisé : 25 USD ;
- alerte : 20 USD ;
- arrêt local : 24 USD ;
- plafond par job : 0,50 USD ;
- plafond par job : 4 appels ;
- prix de calcul : 2 USD/M tokens d'entrée, 0,20 USD/M en cache et
  12 USD/M tokens de sortie.

Le journal persistant contient l'identité du job, le rôle, le modèle, les
tokens, le coût et le résultat de l'appel. Chaque ligne porte aussi un
`requestId` propre, le `traceId` du job, la durée, le nombre de tentatives HTTP
et de retries. Le TTFT est explicitement nul sur ce chemin JSON non streamé. Le
journal ne contient ni prompt, ni preuve, ni endpoint, ni secret. Une réponse
sans métriques d'usage est comptabilisée au montant réservé afin de rester
conservatrice.

Les tarifs ont été revérifiés le 11 septembre 2026 sur la
[fiche officielle GPT-5.6 Terra](https://developers.openai.com/api/docs/models/gpt-5.6-terra) :
2 USD/M tokens d'entrée, 0,20 USD/M en cache et 12 USD/M tokens de sortie.
Un appel de 7 000 tokens d'entrée et 1 000 tokens de sortie coûte donc
0,026 USD, soit environ 961 appels pour 25 USD. Ce nombre représente des appels
API, pas automatiquement des questions SAAIA : un job avancé réussi appelle
normalement Terra deux fois, une fois pour le plan et une fois pour la rédaction.
Le coût est calculé sur l'usage réel renvoyé par le fournisseur. Le plafond de
0,50 USD par job interrompt un cas atypiquement volumineux avant qu'il ne puisse
consommer seul une part importante de la campagne.

Le prix majoré annoncé au-delà de 272 000 tokens d'entrée ne devrait pas être
atteint par ce profil : le prompt de preuves est borné à 240 000 caractères,
soit environ 60 000 tokens selon l'estimation conservatrice du garde-fou, avant
les instructions et la demande.

## Déploiement

La section signée `AdvancedAnalysis` choisit le profil. Les secrets utilisent
les mécanismes `SecretRef` du backend et ne sont pas placés dans le JSON
versionné. Les variables d'installation sont décrites dans
`infra/runbooks/13_advanced_analysis_llm.md`.

Pour la cible on-prem, `infra/docker-compose.advanced-llm.yml` fournit un
service llama-server remplaçable. Son image, son modèle, sa quantification, sa
fenêtre de contexte et ses paramètres GPU sont des entrées de déploiement. Le
service est raccordé au réseau privé Docker de SAAIA ; le port localhost sert
uniquement au diagnostic sur l'hôte.

## Preuves déjà acquises et preuves manquantes

Les tests simulés couvrent le cycle planner -> tools -> writer, le profil
OpenAI, le profil interne, le rejet HTTP externe, l'absence de preuve, une
citation forgée, la non-divulgation des corps d'erreur et les coupe-circuits de
budget. Les erreurs pendant la lecture du corps HTTP sont normalisées comme
timeout ou rupture de transport, tandis qu'une annulation appelant reste
distincte. WinUI explique séparément le timeout et le service injoignable sans
publier le payload fournisseur ni de carte source. Le harnais produit démarre
le client avec le petit modèle local, crée une vraie session backend et vérifie
le fournisseur, le modèle, les appels, les tokens et le coût renvoyés par le job
avancé.

Le parcours Terra réel a produit des résultats durables. Deux cas unitaires ont
été acceptés après revue des preuves. Le planning 5 × 4 a révélé une relation de
cellule insuffisamment prouvée ; le contrat Writer a été durci dans `b20fcc2`.
La campagne complète sur ce code corrigé reste bloquée par le plafond OpenAI
Free de 50 RPD, malgré l'achat de crédits et une escalade au support.

La validation finale exige encore :

1. trois résultats Terra sémantiquement acceptés du planning 5 x 4 sur
   `b20fcc2` ou un descendant documentaire, puis la banque avancée complète ;
2. la validation terminale WinUI du résultat avancé et de ses cartes source ;
3. le même protocole avec un modèle open source sur RunPod ;
4. la sélection du modèle final et son essai sur le serveur on-prem du client ;
5. un holdout aveugle après gel du code.

Tant que ces preuves ne sont pas réunies, le statut reste
`TESTE_NON_APPROUVE`.
