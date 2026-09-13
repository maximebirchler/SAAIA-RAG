# A804 — Préserver les preuves utiles d'une grille

État : TESTE_NON_APPROUVE. La visibilité des preuves est améliorée ; la réponse
du modèle doit encore être validée en conditions réelles.

## Cause isolée

Le job A803 a effectivement demandé deux reprises de recherche pendant Writer,
pour 27 recherches persistées au total et sept appels Terra. Il a terminé par
une insuffisance sur cinq petits-déjeuners, sans planning, pour 0,4949849 USD.
Il n'y a eu ni erreur de crédits ni erreur HTTP du fournisseur.

Une reconstitution hors ligne emploie les événements consommés de ce job,
leur texte canonique et les véritables méthodes du provider pour classer et
projeter les preuves. Elle montre deux défauts : la coupe à 700 caractères
d'un passage de pancakes retire sa mention explicite d'usage au petit-déjeuner,
placée après le caractère 1 000 ; des corps de recette trouvés par les requêtes
ciblées restent hors du contexte du modèle, dont celui des crêpes.

Cette reconstitution vérifie la projection documentaire, sans constituer une
archive exacte des requêtes API ni une réponse sémantique du modèle.

## Correction et comparaison

Chaque passage peut désormais conserver jusqu'à 2 400 caractères, y compris
pour une grille de candidats nommés. La limite totale adaptative conserve son
plafond de 64 000 caractères. Le minimum de contexte croît avec les requêtes
et les unités demandées ; les premières revues peuvent donc recevoir davantage
de texte qu'avant, toujours dans cette borne.

La sélection conserve l'alternance par requête entre titres et textes sources,
mais classe d'abord selon la pertinence du texte pour la requête courante.
Le nombre de requêtes ayant trouvé une preuve devient un départage secondaire.
Une preuve pertinente ne perd donc plus sa priorité au seul motif qu'elle a
été retrouvée plusieurs fois. Les scores sont mis en cache pendant le classement.

Sur les mêmes preuves du job A803, la dernière projection contient exactement
64 000 caractères avant et après. Le passage de pancakes passe de 700 à ses
1 204 caractères complets, rendant sa mention d'usage visible. Un autre corps
de pancakes et le corps des crêpes, auparavant absents, deviennent visibles.
Les passages coupés passent de 45 à un. Cela ne prouve pas encore que tous les
candidats nécessaires seront retenus ni que le modèle les choisira correctement.

Preuve vérifiée :
`artifacts/reprise-pc-20260908/a800-meal-planning-continuity-20260913/meal-a804-causal-projection-comparison.v1.json`.
Le replay A804 v3 porte le plafond par passage vérifié et le hash de l'assembly ;
les deux replays précédents avaient conservé une étiquette de métadonnées
obsolète. Ils sont préservés comme essais intermédiaires.

## Tests et suite

Deux contrôles reproduisent les défauts avant correction ; cinq contrôles
ciblés passent ensuite, dont la coupe explicite d'un passage encore trop long
et l'alternance existante. Suite backend Release : 2 244 réussites, zéro échec,
trois tests live ignorés. Aucun de ces contrôles ni le replay ne consomme l'API.

La suite est un seul diagnostic de planning sur le code figé, suivi de l'examen
des choix et de leurs preuves exactes. Les anciennes banques ne sont pas
réévaluées en tant qu'acceptation aveugle. Aucune question aveugle future n'est
générée pour adapter cette correction. Le budget reste limité aux crédits déjà
achetés, sans achat ni déploiement supplémentaire.
