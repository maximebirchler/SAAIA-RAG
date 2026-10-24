# Validation du client, de son état et de ses sondes — 24 octobre 2026

Le lot client est implémenté et vérifié hors ligne. La dernière campagne complète passe : **69 tests Core et 2 277 tests Client**, sans échec ni test ignoré. Le vrai client Release x64 compile séparément avec `SAAIATestHooks=false`, sans avertissement ni erreur. Ces preuves valident les contrats mécaniques décrits ici ; le produit conserve le statut **TESTE_NON_APPROUVE**. Aucun appel de modèle payant ni changement du serveur n'a été effectué dans ce lot.

## 1. État d'exécution et étapes réellement séparés

Le runner réutilisable garde ses dépendances immuables. `RunExecutionState` est alloué une fois par requête ; `IterationExecutionState` est créé pour chaque tour. Les variables qui traversent les étapes appartiennent à ces propriétaires, les variables utilisées dans une seule étape restent locales. La mémoire conversationnelle reste un contrat d'entrée/sortie distinct du journal diagnostique.

`RunCoreAsync` est passé de **2 335 à 55 lignes**. Le fichier principal fait 114 lignes. Il appelle un vrai pipeline Core qui exécute quatre étapes et s'arrête sur une issue explicite :

| Étape | Responsabilité | Lignes de méthode |
|---|---|---:|
| Préparation | Gates de collecte, budget, checkpoint d'adéquation et intention du tour | 140 |
| Acquisition | Action initiale, choix du menu d'outils, appel modèle, protocole de writer et récupération de contexte | 894 |
| Soumissions et outils | Clarification/insuffisance/sélection, exécution et application des observations | 702 |
| Vérification et revue | Vérification des citations, revue sémantique, révision et arrêt | 474 |

Les issues `Advance`, `Continue`, `Stop` et `Complete` sont exclusives. Seule `Advance` autorise l'étape suivante. Les transitions de cycle de vie distinguent initialisation, exécution, outils, finalisation, échec et annulation. Les politiques mécaniques `AgentIterationPlanner`, `AgentToolMenuPlanner` et `EvidenceAdequacyCheckpoint` sont effectivement utilisées par ce chemin de production.

La preuve Roslyn conserve l'ordre des **83 instructions principales**, soit **650 instructions avec leurs blocs imbriqués**. Les 23 `continue`, 9 `break` et 6 `return` visant l'ancienne boucle sont convertis en issues typées ; les contrôles des boucles, switch et fonctions intérieurs sont conservés. Le remplacement des variables repose sur leurs symboles, pour respecter les portées et les captures des lambdas. L'original et le vérificateur sont conservés sous `artifacts/client-cleanup-20261024/ExtractRun/`.

Le test de concurrence lance deux requêtes réellement chevauchées sur le même runner. Il vérifie la séparation des preuves, des budgets de tour et des traces. La preuve de production inspecte également les champs des **142 parties** du runner : aucun champ d'exécution mutable n'est ajouté au service réutilisable.

La revue indépendante a découvert deux défauts supplémentaires d'ownership, corrigés dans ce gel commun :

- Le chargement d'une discussion pouvait réhydrater la mémoire pendant un envoi. Une barrière Core possède maintenant l'admission chargement/envoi, couvre les entrées bouton/clavier/commandes ainsi que les opérations de session et la connexion, et conserve la session capturée pour le tour. Demander l'annulation ne libère pas prématurément la mémoire. Sept tests Core couvrent notamment lecture échouée, ancienne lease, double disposition, admissions concurrentes et annulation avant démarrage du modèle.
- Deux agents abonnés au même fournisseur pouvaient attribuer les mêmes métriques au contexte ambiant. L'abonnement diagnostique utilise désormais le couple trace/tour du propriétaire et émet dans cet état explicite. Les événements sans identité ne sont pas attribués arbitrairement. Les tests couvrent abonnés partagés, callback hors contexte et observateur défaillant.

Les trois derniers défauts UI sont inclus dans la campagne finale :

- Le token d'annulation appartient à la lease dès l'admission du tour, avant tout `await`. Une annulation pendant la persistance de la question ne peut plus annuler un ancien CTS puis laisser démarrer le modèle avec un nouveau. L'écriture durable est conservée ; le token est vérifié avant les étapes suivantes et avant le modèle. Le tour suivant possède son propre token.
- `RefreshSessionsAsync` ne modifie plus l'identité de discussion : rafraîchir une liste tronquée à 200 entrées ne peut pas remplacer silencieusement l'ID courant par le premier résultat. Le choix de connexion est explicite et `LoadSessionCoreAsync` engage l'identité avec la mémoire réellement chargée.
- `LoadSettings` ne restaure `LastSessionId` que préconnexion, lorsque l'agent est absent. Les chemins setup/repair qui relisent les réglages après connexion ne peuvent plus changer l'ID sous une mémoire déjà connectée.

## 2. Noyau portable et transport avancé

`SAAIA.Client.Core` cible .NET 8 sans WinUI. Il contient le journal diagnostique, le masquage, l'identité des sources, le polling avancé, le cycle d'exécution, le pipeline d'étapes et les politiques mécaniques. La lecture du protocole natif et ses noms de routes sont également portables. Les prompts et catalogues encore dépendants des contrats WinUI ont leur propre responsabilité hors de l'orchestrateur.

`AdvancedAnalysisClientService` dépend d'`ApiClient`, sans dépendance LLM. Le suivi UI ne construit plus un orchestrateur avec `llm: null!`. Le poller portable possède les délais, retries, limites et contrôles d'identité/révision avant application d'un snapshot. Un échec de transport ne devient pas une réponse réussie ; une annulation du suivi local ne prétend pas annuler le job durable du serveur.

Les tests injectent transport, délai et disponibilité. Ils vérifient les reprises, les révisions obsolètes, les mauvais identifiants, les limites d'attente, les timeouts et l'annulation sans serveur réel.

## 3. Sondes, historique et exports support

| Point de l'audit | Comportement mis en place |
|---|---|
| S01 | Historique immuable borné, indépendant de l'orchestrateur recréé ; `lastN` filtre les tours terminés retenus, y compris échec/annulation. Les opérations source et reprise sont identifiées séparément. |
| S02 | Propriétaire UI avant stockage de la question, clôture après affectation du résultat et persistance. Temps local et analyse avancée sont distincts ; les mesures absentes restent nulles. |
| S04 | Trace racine, tour/session et parent W3C ; propagation par ApiClient ; job/handoff et origine conservés pour la reprise et l'ouverture de source. |
| S14 | Événements par tentative, réponse, retry, attente et issue du polling, avec durée et motif codé. |
| S16 | Bilan parsing : entrées reçues, invalides, dédupliquées, visibles et identités manquantes, sans texte documentaire dans les nouvelles sondes ordinaires. |
| S17 | `SourceLocator` commun aux cartes et liens ; identité exacte conservée quand connue, refus des révisions ambiguës, chronométrage de résolution/validation/lancement. |
| S18–S19 | Les deux entrées d'export incluent le même historique, la configuration effective, les limites et la santé des collecteurs, plus un manifeste des pièces présentes/absentes/échouées. |
| S24 | Sondes présentes dans le binaire standard ; callbacks de diagnostic protégés ; échecs/omissions/troncatures comptés ; collecte facultative partielle déclarée. |

Le journal retient par défaut 64 opérations et 240 événements par opération. Les métadonnées ont des bornes explicites : 32 champs par catégorie, 48 mesures par événement, clés de 64 caractères et valeurs de 256 caractères. La rétention est bornée ; elle n'est pas une base historique illimitée et ne survit pas intégralement à un redémarrage du processus. Les logs tournants restent la persistance disponible.

Les callbacks du cycle d'exécution, des phases et du sink de traces ne peuvent plus remplacer le résultat métier ni son exception originale. Les pannes des collecteurs sont comptées, même en cas d'échec ou d'annulation. `trace_sink_configured` distingue un sink présent d'une simple absence d'échec observé. Les tests invoquent le vrai `RunAsync` avec sink défaillant, comparent le résultat et conservent exactement l'exception d'outil d'origine.

L'export exclut le contenu historique brut par défaut. Les nouvelles sondes contiennent des codes, compteurs, durées et identifiants ; le mode contenu exige une activation explicite. Les secrets restent masqués dans ce mode. Les empreintes des copies modifiées par le masquage sont recalculées et l'empreinte source est conservée séparément.

Les tests d'export provoquent un fichier de log verrouillé puis une erreur du writer readiness. L'archive reste produite avec ses autres preuves, `collectionComplete=false`, et un chemin précis de la pièce fautive. Les pièces homonymes de `governance/` et `llm/` ne sont plus confondues. Une collecte partielle n'est pas présentée comme complète.

## Nettoyage vérifié

Le manifeste de suppression client répertorie **94 méthodes privées**, **3 276 lignes de méthodes**, sept fichiers exclus ou vides et une constante sans référence. Le vieux dialogue de paramètres XAML était explicitement exclu de la compilation ; son implémentation active reste en place. Les événements XAML, références en chaînes, méthodes génériques et hooks portant encore des scénarios ont été conservés.

La preuve combine syntaxe, références C#/XAML/scripts et absence de dispatch réflexif en production. Les helpers devenus orphelins ont fait l'objet de nouvelles passes. Les commentaires obsolètes de packaging et de limite d'outils ont été corrigés. Aucun nettoyage aveugle ni reset du checkout partagé n'a été utilisé.

## Preuves finales

- Campagne : `artifacts/client-cleanup-20261024/full-unit-session-final-freeze/manifest.json`, run `27c6a30807d84df6a48ab508b7a18d27`. Compilation incluse, sans restore ; sources identiques avant/après. **69/69 Core et 2 277/2 277 Client**, aucun ignoré. Les campagnes précédentes `a042b09c…` et `7ee863fd…` sont des preuves intermédiaires qui ne couvrent pas tous les derniers correctifs UI.
- Les TRX, leurs empreintes et les DLL réellement exécutées sont référencés dans ce manifeste. Les premières passes échouées sont conservées : assertions sur anciens emplacements puis overload xUnit d'un nouveau test, corrigés avant la passe acceptée.
- Production : `production-release-build.log` et `production-release-proof.json`. Release x64 sans hooks, zéro avertissement/erreur ; diagnostics présents, Core référencé et hook conditionnel contrôlé absent.
- Préservation : `runner-phase-extraction-proof.json` et vérification reproductible `ExtractRun --verify`.
- Structure : `architecture-final-accepted.json` passe. Les agrégats physiques mesurés sont 79 515 lignes pour ToolAgentOrchestrator, 31 918 pour le runner et 15 120 pour MainWindow ; les plafonds sont les mesures plus 256 lignes justifiées.

## Limites conservées explicitement

Les gros agrégats de compatibilité restent volumineux et 21 fichiers dépassent le seuil de revue de 2 500 lignes, sans dépasser le plafond dur de 9 999. L'acquisition et le traitement des outils gardent beaucoup de branches de protocole héritées. La séparation des propriétaires et étapes est réelle ; elle ne constitue pas une réécriture sémantique de toutes ces branches.

Les tests de garde UI exercent le noyau d'admission et les contrats compilés ; ils ne pilotent pas une vraie fenêtre WinUI. Un lancement accepté par Windows ne prouve pas le rendu d'une page PDF. Le temps UI s'arrête à l'affectation du résultat et au stockage, pas à la présentation des pixels. Une reprise après arrêt du processus a sa propre opération corrélée ; aucune durée continue n'est fabriquée à partir d'horloges de machines différentes.

Cette campagne ne prouve pas la qualité d'une réponse du modèle sur le corpus réel, ni la pertinence de toutes les contraintes sémantiques héritées. Elle fournit les frontières, contrats et observations nécessaires pour que la prochaine validation réelle distingue problème de recherche, de contexte, de protocole, de modèle, de transport ou d'affichage.
