# A867 — audit des entrées et réautorisation budgétaire du pilote A861

## Verdict avant appel payant

Le pilote Candidate Explorer reste sémantiquement identique à A861. Le budget
global autorisé passe seulement de 40 à 45 USD après l'ajout de 5 USD confirmé
par l'utilisateur. Aucun appel OpenAI n'a été exécuté pendant cet audit.

Le contrôle préalable ne révèle pas de corpus absent, de référence historique
périmée, d'option Explorer perdue ou de contrat d'outil incohérent qui rendrait
le prochain essai non interprétable. Le pilote peut donc être exécuté une fois,
après commit propre et observation Tier 1 âgée de moins de quinze minutes.

## Contrat réellement présenté au modèle

La banque gelée transmet la demande française de planning du lundi au vendredi,
avec petit-déjeuner, déjeuner, collation et souper, vingt cellules attendues et
une interdiction explicite d'inventer. Le Planner reçoit les lignes, colonnes,
coordonnées de claims, catégories disponibles et huit requêtes maximum.

Le chemin agent expose à Terra des fonctions structurées et strictes :

- `search_corpus` pour la recherche vectorielle dans le corpus privé ;
- `read_source` pour lire jusqu'à quatre pages physiques d'une révision déjà
  observée ;
- `find_source_text` pour retrouver un titre ou des mots littéraux dans cette
  révision ;
- `save_research_state` pour conserver les décisions de recherche ;
- `save_candidate_inventory` pour conserver les titres exacts, rôles proposés,
  localisateurs et EvidenceId de corps.

Le Candidate Explorer est séparé du Writer. Il doit suivre les entrées de
sommaire jusqu'à un corps canonique, atteindre vingt candidats distincts avec
cinq corps proposés par rôle, et conserver deux réserves par rôle lorsque le
budget le permet. Le code ne choisit pas les recettes : il valide uniquement
les identités, les preuves, les comptes, les bornes et la continuité du dossier.

Le plafond de sept appels réserve Planner, jusqu'à quatre tours Explorer, Writer
et Critic. Chaque tour Explorer peut demander plusieurs opérations documentaires
dans le plafond mécanique global de trente-deux outils. Les sorties Explorer,
Writer et Critic disposent chacune de 4 096 tokens ; l'historique natif est
borné à 32 768 caractères. Un lot réaliste de vingt candidats dépasse l'ancien
plafond de 8 192 caractères mais reste accepté sous la borne actuelle.

## Corpus observé le 28 septembre 2026

Le scellement en lecture seule a observé :

- 305 documents et 31 catégories ;
- zéro ingestion active ;
- Qdrant `green`, HTTP 200 ;
- 120 130 points et 120 130 vecteurs ;
- SHA-256 composite
  `7B119CDB072F909236CF3291790B0E287ECCEF48716FC97E338CE8C7EFB798E2`.

La relecture native des vingt références historiques A813/A817, en transaction
PostgreSQL forcée en lecture seule et avec HTTP interdit, retourne vingt
résultats non vides, vingt appels d'outil et 221 preuves cumulées. Le SHA-256 de
la capture privée est
`86CF68967E24F3D2C86D40327228BF916FFDE6C291F258F98DF37A236FA75CD7`.
Quatre recherches littérales de contrôle retournent respectivement 5, 7, 2 et
2 passages canoniques. Ces mesures prouvent la disponibilité actuelle des corps
connus, pas la capacité autonome de Terra à les retrouver.

Les captures privées sont conservées sous
`artifacts/reprise-pc-20260908/a867-input-audit-20260928/` et ne doivent pas être
commitées. Le sceau public ne contient ni extrait documentaire ni secret.

## Intégrité de la configuration

- banque :
  `8B2A5029E0A74D78FF7F26A8F1AACE2D005E84E729E9241AB94FE1BC5AEFFA3E` ;
- configuration provider :
  `036D6C68D1780DC78E3FB594C27FF82CE86861F9BECD94E268AEADE6163D1E1D` ;
- registre OpenAI avant reprise : 1 127 écritures, 39,99436060 USD,
  `79665AC09BE37F2D83FC7D455391F8CB13781E1BD7769EC18245CF5C8CB4D0CC` ;
- clé OpenAI présente dans le coffre DPAPI ;
- runtime local et modèle Qwen qualifiés présents ;
- limites OpenAI observées en lecture seule : Tier 1, Terra à 500 000 TPM,
  500 RPM et 900 000 TPD.

## Enveloppe réautorisée

- plafond global autorisé : 45 USD ;
- alerte douce : 44 USD ;
- arrêt dur : 45 USD ;
- marge calculée avant le premier pilote : 5,00563940 USD ;
- plafond du premier job A861 : 0,75 USD et sept appels ;
- achats et recharge automatique : interdits.

Le premier pilote doit être analysé avant toute répétition. Une réussite humaine
et mécanique autorise les répétitions deux et trois. Un échec autorise seulement
la correction ou l'expérience diagnostique qui répond à la cause observée.
