# A879 — registre d'affectation distincte et replays A817/A877/A878

## Verdict du palier

Le premier composant de l'architecture décidée après A878 est implémenté et
validé hors réseau. Le backend sait maintenant démontrer qu'un ensemble de
candidats déjà qualifiés par le modèle peut réellement remplir toutes les
coordonnées demandées sans réutiliser le même candidat. Les comptes bruts par
rôle ne suffisent plus à déclarer le dossier prêt.

Ce palier ferme un défaut mécanique précis, mais il ne valide pas encore le
planning de repas end-to-end. Le Navigator et le Candidate Judge séparés ne
sont pas encore implémentés, aucun appel OpenAI n'a été effectué et le produit
reste `TESTE_NON_APPROUVE`.

## Défaut corrigé

L'ancien garde exigeait vingt candidats éligibles et cinq occurrences pour
chacun des quatre rôles. Ce critère pouvait être faux lorsque les mêmes
candidats apparaissaient dans plusieurs rôles. Un cas construit avec quinze
candidats réservés au premier rôle et cinq candidats partagés entre les quatre
rôles respectait tous les anciens compteurs : vingt candidats distincts et au
moins cinq occurrences par rôle. Il ne pouvait pourtant remplir que dix des
vingt coordonnées avec des candidats distincts.

`CandidateCoverageSolver` construit désormais le graphe entre coordonnées et
candidats à partir des `targetRoles` décidés par le modèle. Un matching biparti
maximal déterministe calcule :

- le nombre maximal de coordonnées simultanément affectables ;
- une affectation distincte réalisable ;
- le déficit restant par rôle ;
- l'état complet uniquement lorsque toutes les coordonnées sont affectées.

Le solveur ne décide pas qu'un candidat convient à un rôle. Il utilise
strictement les rôles enregistrés par le modèle et ne réalise que
l'affectation, l'unicité et le comptage mécaniques autorisés par le Goal.

## Intégration dans la boucle avancée

Candidate Inventory passe au contrat `candidate-inventory.v2`. Le prompt expose
maintenant `maximumAssignableCount`, `missingByRole` et
`proposedAssignments`, en plus des comptes bruts. Le Candidate Explorer ne peut
plus terminer sur `candidate_dossier_ready` si le matching n'est pas complet.

Le dossier remis au Writer contient la même affectation réalisable. Le Writer
reçoit l'instruction d'utiliser ces candidats distincts ; le raffinement d'un
titre subordonné reste possible seulement lorsque le même corps canonique
prouve l'identité complète, conformément aux gardes déjà validés.

Un contrôleur de déficit expose aussi `candidateAssignmentGap` pendant
l'exploration. Il indique au modèle les seuls rôles dont l'affectation globale
reste incomplète. Le modèle conserve le choix sémantique des titres et des
outils ; le backend recalcule le matching après chaque mise à jour du registre.

Les collections plates gardent leur règle précédente : le nombre global de
candidats distincts suffit lorsqu'il n'existe aucune coordonnée structurée.

## Replays anonymisés

La fixture `candidate_coverage_replays.v1.json` ne contient ni titre, ni
extrait documentaire, ni chemin source, ni EvidenceId. Elle conserve seulement
des clés artificielles et la topologie des rôles nécessaire au calcul. Les SHA
des artefacts locaux d'origine assurent la provenance sans recopier le contenu
privé.

Le solveur retrouve les frontières attendues :

| Replay | Corps/candidats représentés | Affectation maximale | Verdict mécanique |
| --- | ---: | ---: | --- |
| A817, dossier connu | 20 | 20/20 | complet |
| A877, registre final | 17 | 17/20 | déficit 3 |
| A878, registre final | 3 | 3/20 | déficit 17 |

Le replay A817 dérive des vingt sélections finales déjà auditées et de leurs
rôles de coordonnées. Il démontre le transport d'une topologie complète, pas
la découverte ni la classification autonome de ces candidats. A877 et A878
rejouent les ensembles de rôles des registres finaux privés après suppression
de toutes les identités documentaires.

SHA-256 de la fixture :
`35F158652414824866CEA8D62CCE454FBE05A81DBEE40ADA98A5A6A8628514F5`.

## Tests exécutés

- 15/15 tests ciblés Candidate Explorer et inventaire ;
- 307/307 tests `OpenAiCompatibleAdvancedAnalysisProviderTests` ;
- 7/7 tests du solveur et des replays après ajout des fixtures ;
- suite complète `RAG.sln` : 10/10 Contracts, 2 471 réussites Backend avec
  trois tests live explicitement ignorés, 2 338 réussites Client ToolAgent avec
  un test live explicitement ignoré ; zéro échec ;
- `git diff --check` propre.

Aucun service temporaire n'a été lancé, aucun appel réseau LLM n'a été fait et
le journal de coût reste à 44,42531530 USD sur le hard stop local de 45 USD.

## Limites et suite

Ce palier empêche un faux succès et fournit une cible compacte au prochain
tour, mais il ne crée pas les candidats manquants. A878 resterait correctement
borné à 3/20 et A877 à 17/20. La cause restante se situe avant le solveur : les
corps doivent être localisés, lus puis classifiés assez tôt et assez
régulièrement.

La suite directe consiste à séparer le flux actuel en deux responsabilités de
modèle : un Navigator recevant les localisateurs et le déficit compact choisit
les entrées exactes ; un Candidate Judge reçoit de petits lots de corps
canoniques et met à jour identité, autonomie et rôles. Le backend exécute les
fenêtres et paginations, puis persiste le registre. Cette séparation doit
d'abord être éprouvée avec des réponses simulées et les replays locaux. Un
nouveau pilote payant ne sera utile qu'après gel de ce contrat et
préenregistrement d'une enveloppe couvrant un parcours complet comparable.
