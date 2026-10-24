# SAAIA — rapport d'assainissement de l'architecture et des diagnostics

Ce chantier répond à la demande du 24 octobre : corriger la structure et les sondes avant de consacrer de nouveaux crédits à des essais de modèle. Le plan a été enregistré avant les changements. Le registre associé suit séparément les **37 constats** de l'audit : A01–A07, V01–V05 et S01–S25, avec fichiers, tests et limites pour chacun.

Le résultat est une base plus explicite et vérifiable : moteur de recherche séparé du HTTP, étapes et propriétaires d'état identifiés, noyau client portable, diagnostics corrélés et bornés, historique SQL des jobs, mesures et campagnes qui distinguent succès, échec, annulation et absence d'exécution. Des défauts concrets ont également été corrigés dans les conversations, l'annulation, les callbacks de diagnostic et la collecte des preuves. Les résultats définitifs et leurs empreintes figurent dans `artifacts/assainissement-20261024/validation-final.json`.

**Cette validation concerne la structure et les contrats logiciels. Elle ne démontre pas que le planning de repas est désormais correct sur le corpus réel. Le produit reste TESTE_NON_APPROUVE.** Aucun modèle payant n'a été appelé dans ce chantier ; aucun crédit n'a été consommé et aucun changement n'a été déployé sur le serveur.

## 1. Ce qui posait problème et ce qui a changé

### Recherche : sortir le moteur des endpoints

Le fichier `RagEndpoints.cs` regroupait environ **36 839 lignes**. Il mélangeait l'adaptation HTTP, la recherche, la sélection, les règles de langage, la navigation documentaire et la projection des résultats. Cela compliquait la revue et poussait le chemin avancé à fabriquer un contexte HTTP pour appeler le moteur.

Le moteur appartient maintenant au dossier `backend/SAAIA.Backend/Retrieval/`. L'entrée reçoit un contexte explicite : tenant, requête, annulation, gouverneur et logger. Les endpoints adaptent les requêtes et réponses HTTP ; le gateway avancé appelle directement le moteur. Le catalogue ne dépend plus de helpers privés des endpoints. Les modèles de transport contiennent les DTO ; la construction des diagnostics a sa propre responsabilité.

La recherche conserve des familles identifiées : politique de requête, canaux, signaux candidats, sélection, titres, expansion lexicale, navigation, métadonnées, projection et indications de réponse. Il s'agit de classes avec des responsabilités distinctes, pas seulement du déplacement du même type géant dans des fichiers `partial`.

La méthode interactive principale est passée de **3 377 à 59 lignes**, avec 36 étapes nommées. Les données qui traversent ces étapes ont des propriétaires explicites : plan, ressources, chronométrage et candidats de la requête. Les composants les plus grands de ce pipeline restent inférieurs à 250 lignes. Les compteurs indiquent ce qui entre et sort de chaque étape ; un canal désactivé est marqué comme désactivé avec sa raison, sans inventer une recherche réussie à zéro résultat.

La préservation a été contrôlée sur les instructions de l'ancienne méthode et de son sous-parcours large : **275 instructions de premier niveau couvertes, aucune manquante**. Cette comparaison a d'ailleurs détecté une sélection de titre omise pendant l'extraction ; elle a été restaurée avant les suites finales. Le classement et les politiques sémantiques héritées n'ont pas été réécrits dans cette opération.

### Client : séparer état du tour, état de conversation et diagnostic

Le projet `SAAIA.Client.Core` apporte des composants .NET 8 sans WinUI : journal, masquage, identité des sources, polling, cycle d'exécution, transitions et politiques mécaniques. La production utilise ces composants ; ils ne constituent pas un second chemin uniquement destiné aux tests.

Le runner conserve cinq dépendances en lecture seule. Les valeurs mutables sont créées pour chaque exécution et chaque itération. Le contexte de budget et de preuves d'une requête ne doit pas déborder sur la suivante. Deux exécutions réellement chevauchées sur le même runner vérifient notamment leurs budgets distincts et la restauration du contexte parent.

La méthode de coordination `RunCoreAsync` est passée de **2 335 à 55 lignes**. Elle appelle quatre phases nommées : préparation, acquisition, soumissions/outils, vérification/revue. Chaque phase retourne une issue typée : poursuivre l'étape suivante, recommencer l'itération, arrêter ou terminer. Les transferts de contrôle de l'ancienne boucle ont été vérifiés ; un échec ou une fin ne doit pas laisser démarrer une phase ultérieure.

La lecture du protocole du routeur natif et les noms des routes sont portables. Les prompts et catalogues d'outils ont leurs propres composants, sans référence de retour vers un orchestrateur propriétaire. Les anciennes instructions et chaînes ont été comparées avant/après. Les adaptateurs de test devenus inutiles ont été retirés après vérification de leurs remplacements.

Le transport d'analyse avancée est autonome. Le suivi UI ne construit plus un orchestrateur avec un faux LLM nul. Le poller gère les tentatives, délais, révisions et identités ; il distingue la fin du suivi local de l'annulation d'un job durable côté serveur.

### Défauts réels de conversation corrigés pendant la revue

Ces corrections dépassent le rangement des fichiers :

1. **Charger une autre discussion pendant un envoi pouvait modifier la mémoire utilisée par le tour actif.** Une barrière d'admission possède maintenant l'opération. Elle couvre envoi, commande directe, chargement, nouvelle conversation, suppression, renommage et connexion. Le tour conserve son identifiant de session. L'annulation ne libère la barrière qu'à la fin effective du traitement.
2. **Un chargement échoué pouvait laisser une conversation partiellement remplacée.** La lecture précède maintenant l'activation de l'identité et de sa mémoire ; un échec garde l'état précédent.
3. **Le bouton d'annulation pouvait viser un ancien token pendant la sauvegarde initiale.** La lease du tour possède son token dès l'admission, avant le premier `await`. Après la sauvegarde durable de la question, le traitement contrôle l'annulation avant de démarrer le modèle. Les commandes directes suivent la même règle.
4. **Rafraîchir la liste pouvait choisir un autre identifiant sans charger ses messages.** Le rafraîchissement reste visuel. L'activation d'une discussion appartient au chargement explicite ; l'absence d'une session parmi les 200 résultats ne change plus silencieusement la conversation active.
5. **Recharger les réglages pouvait réinjecter un ancien identifiant dans un agent déjà connecté.** La restauration de la préférence de dernière session est désormais limitée à la préparation avant connexion.

Les tests Core couvrent l'admission, la libération idempotente, les anciennes leases, l'annulation pendant la préparation et la séparation entre ancien et nouveau token. La revue indépendante vérifie les appels UI. Elle reste distincte d'un pilotage réel d'une fenêtre WinUI installée.

### Ingestion et migrations

L'ingestion distingue extraction, projection et publication, avec entrées/sorties identifiées. Les compteurs partiels sont finalisés aussi sur erreur ou annulation. La publication atomique reste sous la responsabilité du dépôt de jobs.

Le migrateur prend un verrou global avant contrôle et application. Il mesure la progression, conserve les empreintes SHA-256 et expose les erreurs/rollback. Une ancienne installation sans empreinte est explicitement une base historique, pas une certification rétroactive du contenu ancien.

La migration **069** ajoute l'historique diagnostique de l'analyse avancée. Aucune ancienne migration n'a été modifiée. La nouvelle migration a été testée dans PostgreSQL isolé ; elle n'a pas été appliquée au serveur existant.

## 2. Les sondes : ce qu'elles permettent réellement de comprendre

Les sondes sont placées aux frontières où une décision ou un résultat change : admission, appel HTTP, recherche, sélection, appel modèle, outil, stockage, polling, ingestion et affichage. Un log à chaque ligne aurait produit du bruit et de la charge ; chaque événement utile porte une opération, une issue, une durée et des identifiants.

| Frontière | Observations disponibles |
|---|---|
| Client complet | Tour depuis l'admission UI et la sauvegarde de la question jusqu'au résultat et sa persistance ; phase locale, délégation avancée et reprise séparées. |
| Corrélation | Trace W3C, tour, session, requête, parent et job ; le header de session reçu du client reste une information non fiable distincte de la session réellement stockée. |
| HTTP | Finalisation dans un `finally`, y compris erreur, annulation et réponse hors endpoint métier ; scope/logger défaillant isolé. |
| Recherche | Étapes évaluées, canaux effectivement exécutés, candidats, sélections, motifs de rejet et omissions ; métadonnées ordinaires sans extrait documentaire. |
| Modèle | Composition, tailles, empreintes, outils exposés, identifiants de preuves, limites et troncatures ; journal indépendant du budget et du solde fournisseur. |
| Agent | Transitions et issue terminale, outils réellement invoqués, erreurs/annulations, santé des observateurs. |
| Jobs | Historique SQL durable de création, lease, reprise, retry et fin ; véritable session du job et parent diagnostique. |
| Disponibilité | Heartbeat et capacité du worker, date de la mesure et requête qui la consulte ; un cache ancien n'est pas présenté comme une mesure actuelle. |
| Polling | Chaque tentative, attente, révision rejetée, délai et issue ; aucune absence de réponse transformée en réussite. |
| Ingestion | Extraction/OCR, projection et publication, compteurs partiels même après échec. |
| Sources | Réception, éléments invalides/dédupliqués/visibles, résolution de l'identité, ambiguïté de révision et acceptation du lancement par Windows. |
| Support | Historique commun, configuration effective, versions, limites et santé des collecteurs ; manifeste de pièces présentes, absentes ou en erreur. |

L'historique client est borné à 64 opérations et 240 événements par opération. Les tableaux de candidats, champs, chaînes, fichiers et captures ont aussi des plafonds. Les omissions et troncatures sont comptées : une collecte incomplète reste visible comme telle.

Une défaillance de logger, métrique, listener d'activité, abonnement LLM ou writer d'export ne doit pas remplacer une réponse métier ni masquer l'exception d'origine. Les tests injectent précisément ces pannes. Les callbacks partagés du fournisseur sont attribués au couple **trace + tour**, y compris hors contexte ambiant. Un abonné défaillant ne bloque pas le suivant ; un événement sans identité n'est pas attribué arbitrairement.

Le contenu exact envoyé au modèle reste une capture explicite, avec rétention bornée. En produit, une panne de capture produit une collecte dégradée déclarée ; une campagne peut demander le mode strict. Le nettoyage ne supprime que les fichiers dont le nom et l'identité correspondent au format possédé par SAAIA. Un fichier de notes ressemblant vaguement à un UUID n'est pas supprimé.

Les erreurs HTTP fournisseur sont lues une seule fois avec une limite de taille. Le diagnostic et la classification de quota utilisent ce même corps. Une erreur de collecte ou une expiration de lecture ne doit pas convertir un vrai HTTP 429 en une autre panne artificielle.

L'export support masque les secrets et exclut le contenu brut par défaut. Un fichier verrouillé ou un writer readiness défaillant laisse les autres pièces disponibles ; le manifeste marque la collecte partielle et la pièce exacte en erreur. Les pièces homonymes de plusieurs catégories restent distinctes.

La sonde de présence documentaire vérifie l'index canonique PostgreSQL, par tenant, révision et intersection pages/chunks. **Elle ne vérifie pas la parité vectorielle Qdrant** ; sa réponse l'annonce. De même, Windows acceptant un lancement PDF ne prouve pas que le lecteur a effectivement affiché la page attendue.

## 3. Nettoyage et gros fichiers

Les suppressions ont été précédées de recherches de références C#, XAML, scripts, chaînes et usages réflexifs, puis de compilations/tests. Le manifeste client recense **94 méthodes privées et 3 276 lignes de méthodes**, sept fichiers exclus ou vides et une constante. Le vieux dialogue de réglages supprimé était exclu de la compilation ; son implémentation active reste présente. Onze helpers privés backend ont également été prouvés inutilisés et retirés. Des adaptateurs de tests morts et une fonction PowerShell sans appel ont été éliminés séparément.

Les commentaires contredisant le comportement ont été corrigés. Les scénarios toujours utiles, même historiques, ont été conservés et leur statut explicité. Le comparateur LLM historique ne devient pas une preuve automatique de qualité du produit.

Trois familles de tests gigantesques ont été réparties sans perte de leurs scénarios. La préservation porte respectivement sur **553, 108 et 314 tests**. Les nouveaux tests d'architecture inspectent des contrats compilés et le comportement quand c'est pertinent, plutôt que l'emplacement exact d'un commentaire ou une mise en forme du code.

Le garde-fou impose moins de 10 000 lignes par fichier et signale toute source dépassant 2 500 lignes pour revue. Aucun fichier ne franchit le plafond ; **21 fichiers** restent au-dessus du seuil de revue. Le plus grand est le comparateur PowerShell historique de **8 393 lignes**. Certaines familles cohérentes de recherche, le projecteur documentaire et des familles de tests restent volumineux ; ce n'est pas présenté comme une preuve de simplicité.

Les agrégats `partial` hérités restent une dette explicite : environ **79 515 lignes de fichiers pour ToolAgentOrchestrator**, **31 918 pour le runner**, et **15 120 pour MainWindow**. Ces totaux incluent imports et déclarations ; ils ne mesurent pas une seule méthode. Les responsabilités, états et frontières critiques ont été extraits, mais toute la politique documentaire historique n'a pas été réécrite. Les phases acquisition et outils du runner gardent respectivement 894 et 702 lignes. Les plafonds d'agrégat empêchent une nouvelle croissance silencieuse et doivent diminuer au fil des extractions ; ils ne certifient pas que cette taille serait idéale.

## 4. Validation et provenance des preuves

| Campagne finale | Réussis | Échecs | Non exécutés |
|---|---:|---:|---:|
| Backend Unit | 2 430 | 0 | 0 |
| Contrats Unit | 20 | 0 | 0 |
| Client Core Unit | 69 | 0 | 0 |
| Client Unit | 2 277 | 0 | 0 |
| PostgreSQL isolé | 263 | 0 | 2 |
| **Total des tests de ces campagnes** | **5 059** | **0** | **2** |

S'y ajoutent **21/21 contrôles des profils/prérequis** et **17/17 contrôles des sondes runtime**. Le contrôle de structure inspecte 1 531 sources : aucune violation du plafond, 21 fichiers conservés pour revue de taille. Les builds backend/contrats et client de production terminent sans avertissement ni erreur.

Les campagnes ont été exécutées sans modèle réel. Les détails définitifs — chemins, nombres, empreintes et dates — sont dans `validation-final.json` et les manifestes qu'il référence. Les suites backend/contrats et PostgreSQL portent sur les mêmes quatre assemblages identifiés par SHA-256. La campagne client finale inclut sa compilation et exige une empreinte des sources stable pendant son exécution. Un build du véritable client Release x64, sans hooks de test, vérifie séparément le chemin de production.

Une réconciliation finale compare également les empreintes courantes de **570 fichiers** backend, contrats et contrats de test partagés avec l'inventaire pris avant le gel backend : toutes correspondent. Les manifestes et instantanés de preuves sont conservés avec leurs octets bruts pour ne pas casser leurs empreintes lors d'une normalisation des fins de ligne par Git.

PostgreSQL 16.14 a été lancé dans un cluster temporaire possédé par le test, sur une interface locale. Son répertoire a été vérifié avant exécution. Il a été arrêté et supprimé, le mot de passe temporaire effacé et l'environnement restauré. Aucun cluster existant ni corpus externe n'a été modifié. Deux anciens oracles sémantiques explicitement désactivés restent comptés comme non exécutés ; ils ne sont pas inclus dans les succès.

Les profils `Unit`, `IntegrationPg`, `LiveLocal` et `LivePaid` ont des frontières explicites. Les tests PostgreSQL absents ne passent plus silencieusement grâce à un retour anticipé. Le profil payant nécessite une campagne gardée et ne se déclenche pas par héritage d'une variable d'environnement. Les contrôles de prérequis ont eux-mêmes été éprouvés sur des connexions ambiguës, alias, doublons et valeurs malformées.

Les sondes de performance ont été testées contre un vrai serveur HTTP local de fixture. Le TTFT correspond au premier contenu SSE non vide ; le débit repose sur une consommation disponible et reste indisponible lorsqu'elle manque. Zéro ou un échantillon ne suffit pas à déclarer une performance stable. Un endpoint disponible ne prouve pas une reprise après panne.

Les premières passes échouées ou antérieures aux dernières corrections restent des éléments d'historique, pas la preuve finale. Les manifestes enregistrent tests réellement découverts, succès/échecs/non-exécutés, TRX, assemblages, sources avant/après et commit de départ avec arbre sale. Une campagne `--no-build` ne prétend pas établir à elle seule la correspondance des sources avec un build précédent ; la limite est écrite dans le manifeste. Les changements client pendant la campagne backend sont distingués des assemblages backend réellement exécutés.

La CI hors ligne et ses profils sont livrés dans le dépôt. Son exécution sur le service distant n'a pas été observée ici : un fichier de workflow n'est pas une preuve de pipeline distant réussi.

## 5. Ce qui n'est pas validé et la suite logique

Cette livraison ne certifie ni une perfection absolue, ni la qualité de toutes les décisions sémantiques héritées. Les suites mécaniques ne remplacent pas le corpus réel, le modèle réel, les latences client/serveur et le parcours visuel de l'application installée.

Les vérifications suivantes restent distinctes : installation et migration sur une cible de test, parcours WinUI réel (envoi, annulation, changement de discussion, délégation/reprise, export), lecture de la bonne page par le lecteur documentaire, intégration des dépendances déployées et parité avec l'index vectoriel. Le journal client en mémoire n'est pas un stockage historique illimité après redémarrage ; les logs tournants et l'historique serveur complètent cette rétention.

La suite utile est de **valider un parcours réel ciblé avant une nouvelle banque payante** : une question simple, une question avec source exacte, une question complexe multisource et le planning de repas, en couvrant aussi annulation et panne de transport. Pour chaque cas, on pourra maintenant suivre admission → recherche → candidats/rejets → contexte et outils exposés → appel modèle → outils suivants → réponse → stockage/affichage. Les nouvelles sondes doivent expliquer l'écart observé à chaque frontière.

Un essai Terra ne devra démarrer qu'après contrôle du paquet réellement envoyé : bonnes sources et révisions, outils utilisables, contexte suffisant, absence de troncature cachée, contraintes de protocole compréhensibles et budget explicite. On ne peut pas déduire du seul nombre de tests verts que ce paquet sera parfait sur tous les documents. Cette mission fournit les moyens de le vérifier et évite de confondre un défaut du code avec une limite du modèle.

## Références de livraison

- `documents/agent/plan-assainissement-architecture-et-sondes-2026-10-24.md` : plan enregistré au départ.
- `documents/agent/suivi-assainissement-2026-10-24.json` : les 37 constats, preuves, tests et limites.
- `documents/agent/validation-client-structure-et-sondes-2026-10-24.md` : détails du client et de sa validation finale.
- `artifacts/assainissement-20261024/validation-final.json` : résultats définitifs consolidés et provenance.
- `artifacts/assainissement-20261024/versionable-evidence.json` : sélection des preuves à conserver dans Git.
- `tools/VALIDATION.md` : reproduction des profils et de PostgreSQL isolé.
