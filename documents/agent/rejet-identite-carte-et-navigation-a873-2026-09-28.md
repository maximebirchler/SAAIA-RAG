# A873 — le Writer raisonne mieux que le titre de carte, mais l'Explorer sous-utilise les sommaires

## Verdict

A873 franchit toutes les barrières de transport et de protocole corrigées entre
A868 et A872. Le Candidate Explorer construit un dossier déclaré prêt, le
Writer effectue une recherche complémentaire puis produit un tableau français
de vingt cellules et vingt claims distincts. Le backend refuse ce tableau avant
Critic avec `advanced_synthesis_candidate_identity_not_supported`.

La réponse ne devait pas être affichée telle quelle. Deux petits-déjeuners,
`POUR LA PÂTE À CRÊPES :` et `Pour les crêpes`, sont des composants ou
sous-sections, pas des propositions autonomes. Le résultat reste donc un rejet
sémantique même si l'erreur terminale constatée provenait d'abord d'un contrat
d'identité trop strict.

## Ce que Terra avait réellement reçu

Le premier prompt Explorer contenait 61 extraits : 54 contenus, deux contenus
mixtes et cinq éléments de navigation. Parmi ces derniers figuraient des index
exploitables avec noms et pages, par exemple une liste de recettes sur la page
67 d'une source, une table « en moins de 45 minutes », un index de plats et une
liste alphabétique de recettes.

Terra disposait de `search_corpus`, `find_source_text`, `read_source`, de clés
de sources opaques, des pages physiques, d'un espace de travail et de
`save_candidate_inventory`. Il pouvait donc suivre un nom du sommaire dans sa
source. Il a préféré trois recherches sémantiques générales. Le problème n'est
ni l'absence d'outil ni l'absence de sommaire ; la stratégie choisie par ce run
n'a pas suffisamment exploité les localisateurs disponibles.

## Défaut de représentation observé

Deux cartes de contenu associaient un corps correct à un sous-titre exact :

- `candidateTitle = Les pâtes`, alors que le corps commence par
  `Timbale de pâtes` ;
- `candidateTitle = Préparation`, alors que le corps commence par
  `Gratin dauphinois`.

L'Explorer a qualifié les corps pour Déjeuner/Souper tout en conservant les
sous-titres. Le Writer a correctement sélectionné les identités complètes
présentes dans ces mêmes corps et a gardé leurs EvidenceId. Le garde de rôle
exigeait pourtant une égalité de titre avec l'inventaire et a refusé ces deux
raffinements. Ce faux rejet a consommé la dernière place avant Critic.

## Coût et intégrité

A873 a consommé six appels : Planner, trois tours Explorer et deux tours
Writer. Son coût est 0,46666140 USD. Le registre global contient 1 153
écritures, totalise 41,61946060 USD et laisse 3,38053940 USD jusqu'au plafond de
45 USD. Son SHA-256 est
`D885067E7AAB447F26778FAC2BA537428A1E1B3545901584570CA344CF5BB576`.

Le corpus avant/après conserve le SHA-256 composite
`7B119CDB072F909236CF3291790B0E287ECCEF48716FC97E338CE8C7EFB798E2`,
305 documents, 31 catégories et 120 130 points/vecteurs Qdrant. Aucun job
avancé ne reste actif, le backend temporaire est arrêté et le port est libre.
Les six traces privées sont conservées sous
`artifacts/reprise-pc-20260908/a873-terra-candidate-explorer-pilot-20260928-164419/`.

## Correction causale

Le garde accepte désormais une identité complète différente du titre de carte
uniquement lorsque :

- le candidat Explorer est déjà qualifié pour le rôle demandé ;
- le claim cite le même EvidenceId de corps ;
- la clé de source est identique ;
- l'identité choisie apparaît comme une ligne complète exacte dans ce corps.

Une phrase partielle, un autre extrait ou un autre rôle restent refusés. Le
prompt Explorer explique aussi qu'un titre de carte peut être un sous-titre de
section et qu'il faut enregistrer l'identité complète visible dans le corps ou
rejeter le composant. Lorsque des entrées nommées de sommaire sont visibles et
que la couverture est incomplète, le modèle reçoit la priorité générale de les
suivre dans leur source avant une nouvelle recherche large ; il garde le choix
des noms et des opérations.

Un test reprend `Les pâtes` / `Timbale de pâtes` et démontre que le même corps
qualifié atteint maintenant le Critic sans passe de correction inutile. La
suite provider réussit 304 tests sur 304. Aucun titre de recette ou résultat
attendu n'est codé dans la logique de production. Le prochain pilote A874 doit
mesurer l'effet end-to-end et la décision réelle du Critic. Le produit reste
`TESTE_NON_APPROUVE`.
