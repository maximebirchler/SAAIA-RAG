# A869 — le Candidate Explorer fonctionne, puis bute sur une identité OCR fragile

## Verdict

A869 franchit le défaut de schéma A868. Terra exécute le Planner puis trois
tours Candidate Explorer via Responses. Il choisit des recherches, observe les
résultats, localise deux titres littéraux et produit un inventaire structuré de
vingt candidats répartis à raison de cinq par créneau demandé.

Le job échoue avant Writer et Critic sur
`advanced_synthesis_research_protocol_invalid`. La cause démontrée n'est pas une
incapacité de Terra à comprendre les outils ou le document. Notre merge exigeait
qu'un titre OCR existant soit recopié caractère par caractère, alors que la clé,
la source, les mots normalisés et les preuves suffisaient déjà à préserver son
identité.

## Déroulement et coût

Le Planner a produit huit requêtes cohérentes couvrant les quatre rôles. Les
appels facturés d'A869 sont :

- Planner : 1 365 tokens d'entrée, 307 de sortie, 0,00396240 USD ;
- Explorer initial : 29 073 tokens d'entrée, 150 de sortie, 0,07448100 USD ;
- Explorer suivi 1 : 30 304 tokens d'entrée, 307 de sortie, 0,07944250 USD ;
- Explorer suivi 2 : 30 688 tokens d'entrée, 2 301 de sortie, 0,10433050 USD.

Le total A869 est 0,26221640 USD. Le registre global contient 1 133 écritures et
atteint 40,26295200 USD sur le plafond autorisé de 45 USD. Il reste 4,73704800
USD. Son SHA-256 est
`00EA76292B37C07FFD23A16CF99297A16D04652F2FFB18F099436303DC1773D4`.

## Comportement observé de Terra

Le premier tour Explorer demande deux recherches générales, l'une pour les
petits-déjeuners et l'autre pour les collations. Le tour suivant exploite les
sources observées et demande des localisations littérales pour `PORRIDGE AUX
FLOCONS D'AVOINE` et `PANCAKES`. Les deux opérations réussissent et rendent les
corps canoniques correspondants.

Le troisième tour enregistre vingt éléments `body_verified`, cinq pour chacun
des rôles Petit-déjeuner, Déjeuner, Collation et Souper. Parmi les choix figurent
des titres de recettes plausibles comme le porridge, les pancakes, le smoothie
vert, les œufs mimosa, le sandwich complet, deux compotes, le curry de crevettes,
le poisson vapeur et plusieurs soupes.

Cette réussite mécanique ne suffit pas à approuver la qualité. Deux candidats de
collation sont des rubriques de conseil (`Une collation!` et `Apporter une
collation`) plutôt que des recettes nommées ; quelques autres entrées sont des
exemples de moments de repas. Le Writer n'ayant jamais été exécuté, on ne sait
pas encore si Terra les aurait retenus dans le planning final ou poursuivi la
recherche. A869 prouve une capacité d'orchestration et expose simultanément un
risque de qualité dans l'entrée et la sélection.

## Défaut d'identité exact

L'inventaire automatique contenait les titres OCR canoniques suivants :

- ` Une collation!` ;
- ` Apporter une collation`.

Terra les a renvoyés sous la forme :

- ` Une collation!` ;
- ` Apporter une collation`.

Les clés, sources, mots normalisés et EvidenceId étaient identiques. Le parseur
avait donc validé l'appel d'outil, mais le merge a ensuite comparé les chaînes
brutes et rejeté tout le lot. Ce contrat demandait au modèle d'être le gardien
d'une donnée canonique que le serveur possédait déjà.

## Correction générale et validation

Pour un candidat déjà connu, le merge vérifie maintenant :

1. la même source opaque ;
2. les mêmes mots après la normalisation canonique existante ;
3. la conservation par le serveur du titre exact déjà enregistré.

Une modification de source ou de mots reste refusée. Seules les variations de
ponctuation, d'espacement ou de glyphes OCR non alphanumériques sont tolérées,
et elles ne remplacent jamais la valeur canonique.

Un test reproduit précisément les glyphes `` et ``, puis vérifie que le titre
canonique initial est conservé avec la nouvelle affectation sémantique. La suite
provider réussit : 300 tests sur 300, zéro échec.

## Intégrité et suite

Le corpus postflight conserve le SHA-256 composite
`7B119CDB072F909236CF3291790B0E287ECCEF48716FC97E338CE8C7EFB798E2`,
identique au préflight. La configuration locale a été restaurée, le backend
temporaire arrêté, les ports de test libérés et aucun job avancé ne reste actif.
Les quatre traces privées, l'audit et les événements d'outils sont conservés sous
`artifacts/reprise-pc-20260908/a869-terra-candidate-explorer-pilot-20260928-181520/`.

Une nouvelle exécution du même pilote après commit propre est justifiée pour
observer la suite qui n'a jamais été atteinte : décision finale de l'Explorer,
Writer, Critic, exactitude des vingt cellules et qualité des sources. Le produit
reste `TESTE_NON_APPROUVE`.
