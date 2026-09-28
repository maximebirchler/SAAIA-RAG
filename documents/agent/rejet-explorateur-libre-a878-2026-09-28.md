# A878 — la boucle Explorer libre reste trop instable pour le planning 5 × 4

## Verdict

A878 reçoit le compteur éligible corrigé et douze appels, soit un cycle
documentaire complet de plus qu'A877. Il exécute trente et une opérations,
termine Planner, Candidate Explorer, Writer et Critic, conserve le corpus et
passe les audits mécanique et d'intégrité. La réponse finale est néanmoins une
insuffisance bornée à trois candidats affectés. Le stress-test reste rejeté et
le produit reste `TESTE_NON_APPROUVE`.

Ce résultat ne prouve pas que Terra est incapable de raisonner sur les
documents. Les campagnes A874, A876 et A877 ont déjà montré qu'il sait choisir
`find_source_text`, suivre des noms de sommaire, lire des pages, vérifier des
corps, affecter des rôles et critiquer une synthèse. A878 montre que la boucle
libre actuelle ne transforme pas ces capacités en un processus assez stable et
économe pour une collecte de vingt éléments distincts.

## Ce qui s'est passé

Le Planner produit huit recherches initiales. L'Explorer sélectionne ensuite
des entrées exactes telles que `Barres de céréales aux fruits rouges`,
`Muffins aux myrtilles`, `Cookies aux noix de pécan`,
`Brioche perdue au chocolat`, `Fruits en beignets`,
`Crêpes épaisses fourrées` et `Omelette aux pommes de terre`. Il emploie donc
les outils attendus et ne reste pas bloqué sur une recherche générique.

La séquence reste inefficace :

- plusieurs recherches littérales identiques sont répétées avec un autre
  `topK` sans exploiter d'abord les pages déjà localisées ;
- deux lectures demandent cinq et neuf pages alors que la fenêtre inclusive
  maximale en autorise quatre ; un appel de récupération corrige une partie
  des plages ;
- l'inventaire automatique accumule trente-quatre corps, mais le modèle
  n'affecte un rôle qu'à trois candidats avec corps et à un localisateur ;
- la seule mise à jour sémantique substantielle de l'inventaire arrive à
  l'avant-dernier appel, trop tard pour exploiter les lacunes restantes.

Le dossier final conserve `PANCAKES` et `PORRIDGE AUX FLOCONS D'AVOINE` pour le
petit-déjeuner, ainsi que `Garbure des Midi-Pyrénées` pour déjeuner ou souper.
`Omelette aux pommes de terre` reste `navigation_only`, car le corps n'a pas
été lié. Writer et Critic décrivent alors correctement une insuffisance de
dix-sept choix et ne fabriquent aucune citation.

## Mesures et budget

A878 utilise douze appels, 333 339 tokens d'entrée, 4 314 tokens de sortie,
31 opérations documentaires, 0,83885830 USD et environ 108,8 secondes. Le
registre global contient 1 193 écritures, totalise 44,42531530 USD sur le hard
stop de 45 USD et laisse 0,57468470 USD. Son SHA-256 est
`26B4ADF4FED88975AAEBA523D7E9AA57CB759B87E5025E07002F5CAAB139CEFE`.

Le reliquat ne couvre pas un pilote end-to-end comparable : A877 a coûté
0,68198040 USD et A878 0,83885830 USD. Le dépenser dans une exécution qui serait
coupée avant Writer/Critic ne produirait pas une preuve comparable. La campagne
payante est donc close sans achat automatique.

Le corpus conserve son sceau composite
`7B119CDB072F909236CF3291790B0E287ECCEF48716FC97E338CE8C7EFB798E2`.
Le backend temporaire est arrêté, les ports sont libres, aucun job avancé ne
reste actif et aucun secret n'est présent dans l'artefact. Les preuves privées
sont sous
`artifacts/reprise-pc-20260908/a878-terra-candidate-explorer-pilot-20260928-172408/`.

## Décision architecturale proposée

La prochaine étape ne doit pas augmenter encore le nombre de tours de la même
conversation Explorer. Elle doit réduire ce que le modèle doit mémoriser et
séparer les décisions sémantiques des opérations mécaniques :

1. Un Navigator LLM reçoit les sommaires, localisateurs et lacunes compactes. Il
   choisit les entrées exactes qu'il juge prometteuses.
2. Le backend exécute et pagine mécaniquement `find_source_text` et
   `read_source`, dans les fenêtres autorisées, sans demander au modèle de
   recopier les mêmes arguments.
3. Un Candidate Judge LLM reçoit de petits lots de corps canoniques. Il décide
   si chaque corps est un choix autonome, conserve son titre complet et affecte
   les rôles compatibles.
4. Un registre déterministe déduplique les identités et calcule une affectation
   réalisable de vingt candidats distincts aux vingt coordonnées. Un matching
   biparti empêche de confondre cinq occurrences par rôle avec vingt choix
   distincts lorsque certains candidats couvrent plusieurs rôles.
5. Le contrôleur redemande au Navigator uniquement les lacunes non satisfaites,
   puis le Writer reçoit un dossier compact déjà réalisable. Le Critic vérifie
   sémantique, preuves et présentation.

Le modèle conserve toutes les décisions de sens : pertinence d'une entrée,
identité complète, autonomie d'un choix, adéquation à un rôle et qualité finale.
Le code ne décide que des fenêtres, déduplications, comptes, budgets, reprise et
transport des preuves. Cette séparation respecte le Goal tout en retirant au
modèle la comptabilité fragile observée pendant A878.

Avant tout nouveau crédit, cette architecture doit être testée hors réseau par
replay des preuves A817, A877 et A878. Un nouveau petit budget API ne devient
utile qu'après réussite de ces replays et gel du protocole, pour trois pilotes
live consécutifs. RunPod ou le futur serveur client vient ensuite pour mesurer
un modèle open source sur le même contrat ; changer d'hébergement avant de
corriger le protocole reproduirait la même boucle instable avec davantage de
GPU.
