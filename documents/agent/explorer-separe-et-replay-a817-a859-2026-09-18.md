# A859 — Explorer séparé et transport du dossier A817

## Verdict du palier

Le provider dispose maintenant d'une phase Candidate Explorer distincte du
Writer. Sur les demandes structurées de candidats nommés, elle constitue un
dossier de corps vérifiés et de lacunes avant la rédaction. Le Writer reçoit
ensuite ce dossier avec l'inventaire, puis le Critic audite sa proposition.

Le chemin est protégé par une option désactivée par défaut. Il est validé hors
réseau sur des contrôles déterministes et sur le dossier A817 déjà connu. Il ne
prouve pas encore qu'un modèle live saura découvrir de façon autonome vingt
candidats. Le planning et le produit restent `TESTE_NON_APPROUVE`.

## Topologie et admission

`NativeCandidateExplorerEnabled` n'est admis qu'avec :

- la recherche adaptative et les outils natifs actifs ;
- la topologie native `agent` et l'espace de travail de recherche ;
- au moins trois appels modèle sans Critic, ou quatre avec Critic ;
- un historique natif d'au moins 32 768 caractères.

Le chemin ordinaire reste inchangé lorsque l'option est désactivée ou lorsque
la demande n'est pas une grille structurée en mode `named_item`.
Le runner de validation produit expose la même option, la réserve par rôle et
les contrôles de cohérence ; un futur pilote peut donc activer exactement cette
topologie sans fabriquer une configuration à la main.

Dans le profil à sept appels utilisé pour les campagnes avancées, la réserve
est : Planner, Explorer et ses recherches, Writer, puis Critic. L'Explorer ne
peut pas consommer les appels réservés au Writer et au Critic. Si sa couverture
est incomplète et qu'une recherche reste autorisée, une déclaration prématurée
de fin est renvoyée au modèle avec les comptes exacts. Un dossier borné n'est
accepté que lorsque la capacité documentaire ou les appels réellement
disponibles ne permettent plus de poursuivre.

## Couverture mécanique sans décision métier codée

Le dossier expose :

- le nombre de candidats distincts dont un corps substantiel est vérifié ;
- la cible globale `answerUnitCount` ;
- pour chaque rôle, le nombre minimal correspondant aux lignes demandées ;
- une cible de réserve configurable, deux candidats supplémentaires par rôle
  par défaut ;
- les EvidenceId de corps conservés pour le Writer.

La condition mécanique de disponibilité exige au moins autant de candidats
distincts que d'unités demandées et le minimum requis pour chaque rôle. Le
modèle reste responsable de dire si un candidat est adapté à ce rôle. Le code
compte cette proposition et vérifie son corps ; il ne classe ni repas, ni
procédure, ni produit.

Un défaut a été trouvé pendant ce contrôle : un extrait récupéré avec une
requête ciblée vers une colonne héritait auparavant automatiquement de cette
colonne dans `targetRoles`. Cela transformait un indice de retrieval en jugement
sémantique. Cette attribution automatique est retirée. Un titre exact découvert
automatiquement est toujours retenu avec son corps, mais ses rôles restent
vides jusqu'à une mise à jour explicite de l'Explorer ou une sélection finale du
Writer.

## Contrat de l'Explorer

L'Explorer n'écrit jamais le livrable utilisateur. Il peut appeler les mêmes
outils canoniques que l'agent de recherche et doit sauvegarder les candidats
utiles dans l'inventaire. Il suit un titre de sommaire vers son corps, conserve
les titres entre changements de focus et termine par un handoff interne strict :

- `candidate_dossier_ready` si la couverture mécanique est atteinte et qu'il
  juge le dossier prêt ;
- `candidate_dossier_bounded` uniquement lorsque la recherche n'est réellement
  plus admise.

Le Writer voit ensuite un objet `candidateDossier` avec l'issue `ready` ou
`bounded_gap`, les comptes et les preuves de corps. Une couverture partielle ne
devient pas une absence documentaire : elle reste explicitement un dossier
borné.

## Plafond natif découvert et corrigé

Le protocole de l'inventaire autorisait jusqu'à 32 mises à jour et 16 384
caractères d'arguments, mais la couche commune rejetait encore tout tableau
d'appels natifs supérieur à 8 192 caractères. Le lot réel de vingt candidats
A817 échouait donc avant même la validation des candidats.

La normalisation utilise maintenant le budget d'historique natif configuré. Le
mode Explorer exige 32 768 caractères afin que le lot de candidats et son
enveloppe puissent être conservés au tour suivant. Les bornes de nombre de
candidats, d'arguments, de preuves et de contexte restent inchangées.

## Replay A817 hors réseau

Les traces privées A817 ont été relues localement sans recopier leur contenu
dans le dépôt. Elles contiennent vingt claims et vingt choix distincts, cinq par
rôle, reliés à vingt-trois EvidenceId : vingt et un corps substantiels et deux
localisateurs de navigation.

Un replay transitoire du vrai provider a exécuté les phases Planner, sauvegarde
Explorer, handoff `ready`, Writer puis Critic. Les vingt candidats ont atteint
le Writer, et Writer/Critic ont chacun conservé les vingt claims et les
vingt-trois références attendues. L'artefact local
`meal-a859-a817-candidate-dossier-assessment.v1.json` ne contient aucun extrait
privé ; il enregistre les SHA-256 des traces, les comptes, le verdict et les
limites.

Ce replay valide le transport du dossier connu. Les candidats ont été
reconstruits depuis la trace auditée : aucun modèle n'a redécouvert les titres,
choisi les lectures ou démontré l'autonomie end-to-end pendant ce contrôle.

## Contrôles déterministes

Les scénarios synthétiques couvrent Chat Completions et Responses avec vingt
candidats et cinq rôles par colonne, le handoff complet Explorer vers Writer et
Critic, le refus d'une déclaration `ready` sous-couverte, l'acceptation d'un
`bounded_gap` seulement après épuisement réel des appels de recherche, et le
refus d'une configuration sans espace de travail natif.

Le contrôle provider final passe avec 297 réussites, zéro échec et aucun appel
réseau. Une première suite backend complète sur le même code de production a
compté 2 455 réussites, zéro échec et trois live ignorés ; elle contenait en
plus le replay privé transitoire A817. La suite finale, après remplacement de
ce replay par des gardes synthétiques sans contenu privé, compte 2 456
réussites, zéro échec et trois live ignorés en 13 min 46 s. Le runner PowerShell
du parcours produit passe aussi l'analyse syntaxique.

## Limites et suite

L'option reste désactivée par défaut parce qu'aucun pilote live ne démontre
encore le comportement du modèle sur ce nouveau prompt. Le prochain palier doit
rejouer les traces Explorer A855/A856 pour vérifier que les titres de sommaire
deviennent bien des recherches de corps et que les lacunes changent de colonne
sans perdre les candidats.

Quand une capacité avancée redevient disponible, un seul pilote connu doit
activer l'Explorer sur un état Git et un corpus gelés. Les deux répétitions ne
sont autorisées que si le premier produit vingt choix adaptés, vingt corps
ouvrables et aucune fausse absence. Le holdout et WinUI restent ensuite
obligatoires.

Le registre OpenAI reste à 39,99436060 USD sur 40. Aucun appel payant et aucun
achat ne sont associés à A859.
