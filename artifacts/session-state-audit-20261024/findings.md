# Audit de propriété requête/session — 24 octobre 2026

## Ce qui appartient réellement à chaque exécution

`RagChatAgent.RunCoreAsync` crée un `ToolAgentOrchestrator` neuf par tour et lui transmet une copie des réglages. Les champs `_last*`, le chronomètre et la séquence des traces, le cache des handlers et le dernier transfert avancé de l'orchestrateur ne sont donc normalement pas partagés entre deux tours de l'interface.

En revanche, `RagChatAgent` possède un unique `ToolMemory` mutable et le réutilise pour ses tours successifs. Les couches sont explicites : préférences, instantanés du workspace, mémoire de session et observations d'exécution. Le reset conserve volontairement les deux premières et efface les deux dernières. C'est la frontière entre le changement de session et l'exécution d'un tour qui devait être rendue exclusive.

Les reprises d'analyses serveur utilisent `AdvancedAnalysisClientService` avec l'identité de session/job capturée ; elles ne réutilisent pas la mémoire de l'agent local pour exécuter un tour. La persistance des snapshots vérifie déjà la correspondance entre la session demandée et `job.SessionId`.

## Défauts concrets observés avant correction

1. **Envoi pendant le chargement d'une autre session.** `LoadSessionAsync` activait `_isLoadingSession`, changeait `_sessionId`, effaçait la mémoire et les messages, puis attendait `ListMessagesAsync`. `UpdateUiState` ne consultait que `_isGenerating`. `InputBox_KeyDown` appelait directement `SendAsync`, qui ne contrôlait ni le chargement ni une génération déjà active. Une requête envoyée durant l'attente pouvait donc démarrer sur un historique vide ; le retour du chargement ajoutait ensuite l'ancien historique et réhydratait la même mémoire pendant le tour.
2. **Création/connexion et opérations de session avant leurs premiers awaits.** Ces parcours n'avaient pas de propriétaire commun avec l'envoi. Le simple drapeau de `LoadSessionAsync` commençait trop tard : un autre tour pouvait démarrer avant la création ou le rafraîchissement de la nouvelle session, ou pendant la reconnexion qui recréait `_agent`.
3. **Renommage d'une autre session.** Le handler rafraîchissait la liste en préférant l'identifiant de la session renommée, ce qui remplaçait `_sessionId` sans charger sa conversation ni réinitialiser sa mémoire. Le prochain message pouvait ainsi être persisté dans cette autre session avec l'ancien contexte.
4. **Identité relue après un await.** Les callbacks et la persistance normale/annulée de `SendAsync`, ainsi que la commande directe, relisaient le champ `_sessionId` plutôt qu'une identité du tour. Cela amplifiait les courses ci-dessus.
5. **Annulation ignorée pendant la préparation.** Le nouveau `_cts` était créé après les attentes de persistance du message utilisateur et d'auto-titrage. Annuler pendant ces attentes touchait le token du tour précédent ; la suite créait ensuite un token non annulé et pouvait lancer le modèle alors que le bouton affichait déjà l'état d'annulation.
6. **Repli d'identité au simple rafraîchissement.** `RefreshSessionsAsync` remplaçait l'identifiant actif par la première session de la liste si la session préférée manquait des 200 résultats, sans charger sa mémoire. De même, les relectures de réglages par setup/repair restauraient l'identifiant persisté alors qu'une mémoire connectée pouvait déjà être active.

Ces chemins ont été suivis dans les fichiers actifs `MainWindow/Sessions.cs`, `Messages.cs`, `StartupSetup.cs`, `ThemeChrome.cs`, `HelpAndLocalization.cs` et `SetupLifecycle.cs`. Ils ne reposent pas sur des anciens helpers inactifs.

## Correction mise en place

`Client.Core/Conversation/ConversationActivityGate` est le propriétaire portable de l'admission : une transition de session ou un tour peut être actif, jamais les deux. Son bail capture l'identité du tour. L'annulation demandée ne libère pas l'admission : le traitement doit réellement atteindre son `finally`. La destruction répétée d'un ancien bail ne peut pas libérer un nouveau propriétaire.

Le bail du tour possède également son token d'annulation dès l'admission. `CancelGeneration` annule ce propriétaire courant ; aucun champ `_cts` global ne peut être remplacé après un await. Envoi et commande directe capturent ce token avant d'activer l'interface en génération. L'envoi préserve l'écriture durable du message utilisateur, puis contrôle l'annulation après cette écriture, après l'auto-titrage et après le réveil local, avant de lancer le modèle. La destruction d'un ancien bail ne touche pas le token d'un nouveau tour.

L'interface utilise cette même barrière pour le chargement, la création, le renommage, la suppression, la connexion, l'envoi et la commande directe. Les champs visibles restent cohérents avec l'admission, les contrôles sont désactivés au début d'une transition, et les points d'entrée vérifient aussi leur admission afin de traiter les événements déjà en file.

Le chargement récupère désormais les données avant de remplacer ensemble l'identifiant, les messages et la mémoire. Un rafraîchissement de liste ne modifie jamais l'identité chargée, y compris lorsque la pagination omet la session active. Connect choisit explicitement une cible, puis la charge ; New/Delete ne sélectionnent pas avant le commit. Les relectures de réglages ne restaurent l'identifiant sauvegardé que lorsque l'agent est absent, avant propriété d'une mémoire connectée. Renommer une autre session conserve la session active. La suppression courante invalide son identifiant et sa mémoire avant une éventuelle lecture suivante échouée ; elle ne laisse donc pas une session supprimée disponible à l'envoi. Les callbacks visuels différés ignorent une sélection capturée devenue obsolète. Les persistences et transferts de l'envoi et des commandes utilisent l'identité capturée du tour.

## Validation et limites

Sept tests portables `ConversationActivityGateTests` ont été ajoutés : chargement avec envoi/deuxième chargement refusés ; annulation sans libération prématurée ; ancienne fin/double destruction incapable de libérer un nouveau propriétaire ; sortie sur échec de lecture ; admission concurrente à 32 demandeurs avec un seul propriétaire ; annulation durant une préparation dont l'écriture est conservée sans appel du modèle ; indépendance des tokens d'un ancien et d'un nouveau tour. Leur exécution et la compilation WinUI sont incluses dans le gate final ClientCore/Client piloté par l'agent client après gel commun. Aucun résultat final n'est anticipé dans cette note.

La revue indépendante des fichiers finaux est favorable : elle a fait corriger le dernier repli d'identité de Refresh et confirmé ensuite la garde des réglages, l'exclusivité du propriétaire, l'origine du token, la libération et les captures de session. Il s'agit d'une revue statique complétée par les contrats portables ; aucun test natif XAML interactif n'a été exécuté dans ce lot.

L'audit AST reproductible (`Program.cs`, `Analyze.csproj`, `orchestrator-state-inventory.json`) relève **239 fichiers partiels actifs, 77 343 lignes, 2 148 méthodes : 1 892 statiques (88,08 %) et 256 d'instance**, avec 29 champs d'instance. La fermeture syntaxique conservatrice trouve 237 méthodes d'instance dépendant directement ou indirectement de champs. Ces mesures ne déclarent pas les fonctions statiques pures : elles peuvent utiliser des caches ou services statiques. Cinq fichiers conditionnels de hooks de tests ne sont pas activés dans cet inventaire de production.

La grande taille de l'agrégat reste une concentration de responsabilités. Ce lot en extrait la responsabilité d'admission réelle et ferme les courses constatées ; les quatre propriétaires du protocole natif extraits précédemment restent une séparation cohérente supplémentaire. L'inventaire ne prétend pas que déplacer davantage de fonctions statiques éliminerait automatiquement un risque de session.

Le callback `CallCompleted` et ses filtres de propriétaire sont traités séparément par l'agent principal. Aucun `RagChatAgent` n'a été modifié par ce lot UI. Aucun appel payant, corpus installé, serveur de production ou commit.
