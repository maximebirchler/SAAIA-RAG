# A870 — le Writer reçoit un dossier trop large et sort des rôles qualifiés

## Verdict

A870 franchit les deux défauts de protocole précédents. Le Candidate Explorer
termine avec un dossier déclaré prêt, puis le Writer demande une lecture ciblée
et produit un tableau en français comportant les vingt cellules attendues.

Le job est néanmoins rejeté avant Critic avec
`advanced_synthesis_candidate_identity_not_supported`. Ce rejet protège le
produit : la réponse répétait une recette, réaffectait plusieurs candidats hors
du rôle validé par l'Explorer et introduisait des titres extérieurs aux vingt
candidats qualifiés.

## Coût et progression réelle

Les six appels A870 ont tous réussi au niveau du provider :

- Planner : 0,00429840 USD ;
- Explorer initial : 0,07460450 USD ;
- Explorer suivi 1 : 0,10191200 USD ;
- Explorer suivi 2 : 0,08209900 USD ;
- Writer initial : 0,08798950 USD ;
- Writer après recherche : 0,09573100 USD.

Le total A870 est 0,44663440 USD. Le registre global contient 1 139 écritures,
totalise 40,70958640 USD et laisse 4,29041360 USD jusqu'au plafond de 45 USD.
Son SHA-256 est
`4F8E5899E541A875C820CF25BA91BA040079A3AD6DD863E31098CD8DB59C37B1`.

L'Explorer a qualifié vingt candidats distincts, cinq pour Petit-déjeuner,
Déjeuner, Collation et Souper. Il a lui-même déclaré la couverture suffisante.
Le Writer a ensuite demandé une lecture canonique supplémentaire, puis a fourni
vingt claims et un tableau complet. Le problème se situe donc à la frontière du
dossier et de la sélection finale, pas dans l'incapacité à produire le format.

## Défauts de la réponse Writer

L'inspection de la sortie privée montre notamment :

- une même recette utilisée deux fois alors que vingt choix distincts sont
  exigés ;
- un candidat qualifié pour le petit-déjeuner placé en collation ;
- un candidat qualifié pour le souper placé au déjeuner ;
- plusieurs choix issus de nouvelles preuves mais absents de l'ensemble que
  l'Explorer avait évalué par rôle.

Le garde lexical et d'unicité a donc correctement empêché l'affichage d'une
réponse séduisante mais contractuellement incorrecte. Le Critic n'a pas été
appelé, car le Writer avait consommé le dernier appel disponible avant la
réservation du Critic.

## Défaut dans les données données au Writer

Le dossier `ready` déclarait `bodyVerifiedDistinctCount = 38`. Ce nombre
comprenait tous les titres automatiquement observés dans les passages, y compris
des rubriques, métadonnées et autres titres sans rôle attribué. L'Explorer avait
pourtant explicitement qualifié seulement vingt candidats.

Le Writer recevait donc :

- les vingt candidats évalués par l'Explorer ;
- dix-huit titres automatiques supplémentaires sans rôle ;
- les preuves prioritaires de l'ensemble des trente-huit corps ;
- la possibilité de faire une recherche et de sélectionner immédiatement un
  nouveau titre sans l'ajouter d'abord au dossier qualifié.

Cette entrée brouillait la séparation prévue entre exploration sémantique et
synthèse. Il aurait été prématuré d'attribuer le résultat uniquement au modèle.

## Correction générale

Pour une collection structurée, la couverture Explorer compte désormais les
candidats `body_verified` ayant au moins un rôle demandé. Les rubriques observées
automatiquement sans rôle restent dans la mémoire interne, mais ne permettent
plus de déclarer le dossier prêt et ne sont plus prioritaires dans l'entrée du
Writer.

Le dossier transporte les clés des candidats admissibles et une consigne de
synthèse explicite : chaque coordonnée doit recevoir un candidat distinct dont
`targetRoles` contient la colonne concernée. Le Writer peut encore rechercher un
meilleur élément ; il doit alors enregistrer ce candidat avec une preuve de corps
et le rôle prévu avant de l'utiliser.

Le contrôle de binding vérifie ce lien après chaque proposition. Une sélection
hors dossier ou affectée au mauvais rôle produit
`candidate_role_not_verified` et donne au modèle une correction ciblée tant que
le budget d'appels le permet.

## Validation locale et intégrité

La suite provider réussit : 301 tests sur 301, zéro échec. Deux preuves nouvelles
sont couvertes :

- une rubrique documentaire sans rôle n'augmente plus la couverture du dossier
  et n'apparaît plus dans l'inventaire prioritaire du Writer ;
- une permutation entre deux rôles, pourtant lexicalement bien sourcée, déclenche
  deux corrections `candidate_role_not_verified`, puis une réponse corrigée est
  acceptée.

Le corpus postflight reste identique au préflight. La configuration locale est
restaurée, le backend temporaire arrêté, les ports libérés et aucun job avancé
ne reste actif. Les six traces privées et l'audit sont conservés sous
`artifacts/reprise-pc-20260908/a870-terra-candidate-explorer-pilot-20260928-182250/`.
Le produit reste `TESTE_NON_APPROUVE`.
