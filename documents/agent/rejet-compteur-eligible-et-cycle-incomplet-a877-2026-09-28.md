# A877 — navigation exacte efficace, compteur Explorer trompeur et cycle incomplet

## Verdict

A877 démontre l'effet positif de la priorité donnée aux entrées nommées. Terra
utilise `find_source_text` sur six noms exacts observés et lit une fenêtre
physique. Il passe des treize candidats affectés d'A876 à dix-sept candidats
distincts affectés à au moins un rôle, avec cinq occurrences disponibles dans
chacun des quatre rôles. La chaîne Planner, Explorer, Writer et Critic termine ;
les audits mécanique et d'intégrité passent.

Le stress-test reste rejeté. Dix-sept candidats distincts ne permettent pas de
remplir vingt cases sans réutilisation. Le Writer rend donc une insuffisance au
lieu d'inventer. Le Critic la conserve mais modifie à tort le décompte en
dix-huit et formule des déficits dont la somme est incohérente. Aucun tableau
5 × 4 n'est livré.

## Cause locale découverte avant de juger Terra

Le dernier prompt Explorer présentait :

- `requiredDistinctCount: 20` ;
- `bodyVerifiedDistinctCount: 42` ;
- cinq corps vérifiés pour chacun des quatre rôles.

Le nombre 42 incluait toutes les cartes automatiquement retenues, y compris les
rubriques et corps auxquels le modèle n'avait affecté aucun rôle demandé. Le
garde local utilisait correctement dix-sept candidats éligibles. Terra a donc
conclu que le dossier possédait vingt candidats distincts, alors que le nombre
affiché dans son contexte ne représentait pas la même population que le garde.
Il a aussi pu additionner les quatre comptes par rôle, bien qu'un même candidat
puisse apparaître dans plusieurs rôles.

Le prompt d'inventaire expose désormais deux valeurs sans ambiguïté :
`bodyVerifiedDistinctCount` compte uniquement les candidats affectés à au moins
un rôle demandé ; `totalBodyVerifiedDistinctCount` conserve le total, y compris
les éléments non affectés. La consigne interdit explicitement d'additionner les
comptes par rôle. Ce changement aligne exactement l'information donnée au
modèle sur le calcul du garde mécanique.

## Enveloppe de recherche

A877 consomme Planner, six appels Explorer, Writer et Critic. Les dix-huit
opérations documentaires comprennent douze recherches initiales, cinq recherches
littérales exactes supplémentaires et une lecture physique. Le dernier appel
Explorer reçoit l'inventaire final lorsque `researchAllowed` est déjà faux : il
ne peut plus rechercher les trois candidats distincts manquants.

Un cycle complet supplémentaire nécessite trois appels modèle : choisir les
opérations, intégrer les résultats dans l'inventaire, puis rendre le terminal
Explorer. Le prochain pilote porte donc l'enveloppe à douze appels. Son plafond
monétaire passe à 1,10 USD afin de ne pas couper ce cycle, tout en restant sous
le solde global autorisé et le hard stop persistant de 45 USD. Il ne s'agit pas
d'une répétition identique : le compteur transmis et la capacité de terminer un
cycle documentaire sont corrigés causalement.

## Coût et preuves

A877 consomme neuf appels, 0,68198040 USD et environ 106,6 secondes. Le registre
global contient 1 181 écritures, totalise 43,58645700 USD et laisse
1,41354300 USD jusqu'au plafond de 45 USD. Son SHA-256 est
`8ADE7FD9160BDC6B14ACA43A2E50738A2E011BEB1B37BDB712518203F0ECDA4E`.

Le corpus est inchangé sous le sceau
`7B119CDB072F909236CF3291790B0E287ECCEF48716FC97E338CE8C7EFB798E2`.
Le backend temporaire est arrêté, les ports sont libres et aucun job avancé ne
reste actif. Les preuves privées sont conservées sous
`artifacts/reprise-pc-20260908/a877-terra-candidate-explorer-pilot-20260928-171654/`.
Le produit reste `TESTE_NON_APPROUVE`.
