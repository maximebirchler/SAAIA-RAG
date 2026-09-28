# A875 — insuffisance honnête, claims de remplissage invalides et Explorer trop court

## Verdict

A875 confirme que le raffinement d'identité A874 est accepté. L'Explorer
termine proprement et transmet un dossier borné de treize candidats : trois
petits-déjeuners, cinq déjeuners, quatre collations et un souper. Il ne compte
plus les composants de crêpes comme des choix autonomes.

Le Writer répond honnêtement qu'il ne peut pas remplir vingt cases à partir de
ce dossier. Le backend refuse néanmoins l'objet avec
`advanced_writer_evidence_id_invalid`. Ce code d'erreur ne vient pas d'un ID
inconnu : les cinq ID cités figurent tous dans les 60 preuves visibles, le
dossier et l'inventaire.

## Cause exacte du refus

Pour respecter mécaniquement les vingt coordonnées, Terra a produit vingt
claims même avec l'outcome `insufficient_documentation`. Quinze de ces claims
décrivaient les cases non couvertes avec `evidenceIds: []`. Le parseur exige à
juste titre qu'un claim documentaire possède une preuve ; une absence de
candidat dans le sous-ensemble exploré n'est pas elle-même une preuve
documentaire.

Le contrat indiquait comment formuler une insuffisance, mais ne disait pas
assez explicitement que le nombre de claims et les coordonnées obligatoires ne
s'appliquent qu'à une réponse `answered`. La consigne commune précise désormais
que `claims` peut être vide pour une insuffisance ou une clarification, qu'aucun
claim de remplissage ne doit être créé et que seules les affirmations positives
avec EvidenceId courants peuvent rester dans les claims. La lacune bornée est
décrite directement dans `answerText`.

## Limite de recherche mesurée

A875 a utilisé le Planner, quatre tours Explorer puis le Writer. Avec sept
appels maximum et un appel Critic réservé, l'Explorer n'avait plus le droit de
rechercher après avoir mesuré le dossier 3/5, 5/5, 4/5, 1/5. Le corpus connu
contient pourtant les vingt corps A813/A817. Cette insuffisance est donc celle
du job borné, pas une absence démontrée du corpus.

Le plafond d'appels passe de sept à neuf sans changer le plafond monétaire de
0,75 USD par job. Deux places supplémentaires permettent au modèle d'effectuer
un nouveau tour documentaire puis de rendre son terminal Explorer, tout en
conservant un Writer et un Critic. La banque, le corpus, le modèle, les outils,
les tokens par appel et les critères d'acceptation restent identiques.

## Coût, tests et intégrité

A875 a consommé six appels, pour 0,40699340 USD. Le registre global contient
1 163 écritures, totalise 42,29377840 USD et laisse 2,70622160 USD jusqu'au
plafond de 45 USD. Son SHA-256 est
`37A873611EF7DFC359D7FC9472C80B2E36288E02174CA665B027F395C0B1BAED`.

La suite provider réussit après correction. Le corpus avant/après conserve le
SHA-256 composite
`7B119CDB072F909236CF3291790B0E287ECCEF48716FC97E338CE8C7EFB798E2`.
Le backend temporaire est arrêté, les ports sont libres et aucun job avancé ne
reste actif. Les six traces privées sont conservées sous
`artifacts/reprise-pc-20260908/a875-terra-candidate-explorer-pilot-20260928-170203/`.

Le prochain pilote A876 doit vérifier si le tour documentaire supplémentaire
réduit le déficit, si l'insuffisance éventuelle passe proprement, et si une
réponse complète atteint enfin le Critic. Le produit reste
`TESTE_NON_APPROUVE`.
