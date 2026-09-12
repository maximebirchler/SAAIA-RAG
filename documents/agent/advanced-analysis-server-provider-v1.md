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

## Authentification, tenant et accès au corpus

Toutes les routes `/advanced-analysis/jobs` passent par
`ApiKeyAuthMiddleware`. La clé n'est jamais utilisée pour choisir un tenant
fourni par le client : son hash résout un `tenant_id` côté PostgreSQL, et le
tenant doit encore être actif. Une clé absente, invalide, révoquée ou rattachée
à un tenant désactivé reçoit une réponse 401. Les lectures et annulations
filtrent ensuite par `tenant_id + user_id + job_id`; une tentative avec la clé
d'un autre tenant reçoit 404 et ne révèle pas l'identité du job.

Le `user_id` reste actuellement un espace de noms déclaré par le client sous
une clé de tenant. Il ne constitue pas une identité utilisateur authentifiée :
deux personnes qui partageraient la même clé de tenant ne sont pas séparées par
un jeton individuel. Un déploiement avec utilisateurs mutuellement non fiables
devra lier la clé ou un futur jeton OIDC/JWT à un sujet utilisateur et dériver
le `user_id` côté serveur. Cette évolution relève du futur parcours
d'installation et d'identité ; l'isolation de tenant exigée pour la capacité
avancée est appliquée aujourd'hui.

Le grand modèle ne reçoit aucun accès réseau direct au corpus. Le worker porte
le tenant du job jusqu'au resolver et au tool gateway ; toutes leurs requêtes
PostgreSQL et RAG incluent ce tenant. Une référence provenant d'un autre tenant
est rejetée avant tout appel fournisseur. Les traces durables contiennent la
requête de recherche et les identités canoniques des preuves, mais aucun texte
d'extrait. Le texte n'est rechargé qu'après revalidation dans le tenant courant.

## Journaux et données observables

Les journaux applicatifs avancés consignent les identifiants de job, tenant et
utilisateur, les états et des codes d'erreur normalisés. Ils ne consignent ni
le prompt, ni le texte du handoff, ni les extraits de preuve, ni une clé. Les
événements d'audit de création et d'annulation enregistrent l'acteur, le job et
les identifiants de contexte utiles, sans contenu documentaire. Le registre de
coût externe reste séparé et ne contient que les identifiants techniques, le
fournisseur, le modèle, les tokens, la durée, le nombre de tentatives, le coût
et l'erreur normalisée.

Les traces d'outils persistées sont soumises à la même expiration que le job et
sont supprimées en cascade. La collecte, l'accès et la durée de conservation
des journaux de l'hôte restent une responsabilité de déploiement : ils doivent
rester sur un stockage protégé et ne pas recevoir un niveau de log qui capture
les corps HTTP ou les paramètres secrets.

## Chiffrement et frontières réseau

Les profils externes sont limités à HTTPS par le provider avant toute requête.
Le profil on-prem par défaut reste sur le réseau Docker privé et le port de
diagnostic du grand modèle est lié à `127.0.0.1`. Si le grand modèle réside sur
un autre hôte, son URL doit passer par HTTPS ou par un tunnel chiffré administré
par le client.

Le backend authentifie toutes les routes avancées avec la clé API SAAIA. Son
exposition hors de l'hôte exige donc un reverse proxy TLS ou un tunnel chiffré ;
sinon la clé, le handoff et le résultat circuleraient en clair. Le Compose sépare
désormais `SAAIA_BIND_ADDR`, réservé au backend, de
`SAAIA_INTERNAL_BIND_ADDR`, maintenu sur loopback pour PostgreSQL, Qdrant et
TEI.

Les données persistantes ne reçoivent pas de chiffrement applicatif propre au
module avancé. PostgreSQL, Qdrant, les documents et les sauvegardes doivent être
placés sur un volume chiffré et protégé par l'infrastructure du client. Le script
de sauvegarde produit actuellement des fichiers en clair ; une destination
chiffrée constitue donc une précondition de déploiement, encore non validée sur
le serveur cible.

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

Le grand modèle reste un projet Compose séparé raccordé au réseau externe créé
par la stack principale. Le runbook ne fusionne plus son fichier avec
`docker-compose.prod.yml` : le dernier `name:` d'une fusion aurait changé le nom
du projet principal et pouvait isoler le backend du service `advanced-llm`. La
stack principale doit créer le réseau, puis le projet avancé est validé et
démarré séparément.

## Rétention des données avancées

Le backend calcule `expires_at` à la création de chaque job avec une durée
configurable de 1 à 365 jours. Un service de rétention dédié supprime par lots
les jobs échus et laisse PostgreSQL supprimer leurs traces d'outils par cascade.
Ce service ne dépend ni de l'activation du worker LLM ni du droit avancé actuel :
une révocation de licence ne suspend donc pas l'effacement des données déjà
stockées.

Un job `running` n'est jamais supprimé pendant un bail actif. Une fois sa durée
de rétention atteinte, le bail ne peut plus être renouvelé ; le provider perd le
droit de publier un résultat et le job devient supprimable après expiration du
bail. Les valeurs signées actuelles exécutent un balayage toutes les cinq minutes
et suppriment jusqu'à dix lots de mille jobs par passage.

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

Le pipeline HTTP Kestrel est aussi testé sur PostgreSQL réel. Les onze tests
d'endpoint avancé incluent désormais une création authentifiée, les refus de
clé absente et invalide, les lectures et annulations inter-tenant refusées, le
cloisonnement par utilisateur, la révocation de clé et la désactivation du
tenant. Cette preuve traverse le vrai middleware d'authentification au lieu
d'injecter artificiellement le tenant dans le contexte du test.

Le profil final `customer-server` a été rejoué sur le SHA `b43c1a8f` contre une
fixture HTTP locale OpenAI-compatible. La séquence live loopback comporte un
Planner valide, un Writer JSON volontairement tronqué et une réparation unique
valide. Elle termine avec exactement trois appels, vingt claims et vingt preuves
distinctes, sans appel externe. La fixture est arrêtée et le port 18081 est libre
après le test. Cette preuve valide le protocole et sa borne de réparation sur la
topologie interne ; elle ne qualifie pas encore la qualité ou la performance du
grand modèle final.

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
