# A857 — direction proposée après la campagne Terra

## Décision proposée

Figer temporairement les prompts Writer et Critic. Ils savent terminer la tâche
quand le bon dossier de preuves leur est remis et le Critic rejette maintenant
les affectations manifestement inadaptées. La prochaine phase doit construire
un **inventaire de candidats explicite** entre la recherche documentaire et le
Writer.

Ce changement reste généraliste. Un candidat peut être une procédure, une
exigence, un produit, un incident ou une recette selon la demande. Le code ne
décide pas qu'un candidat convient sémantiquement ; il conserve la décision du
modèle, son état documentaire et les preuves qui la rendent contrôlable.

## Contrat de l'inventaire

Chaque entrée doit au moins conserver :

- une identité stable de candidat et son titre exact observé ;
- la source, la révision et la position où le candidat a été découvert ;
- l'état `navigation_only`, `body_requested`, `body_verified`, `rejected` ou
  `selected` ;
- les rôles ou sous-objectifs proposés par le modèle et une justification
  courte ;
- les EvidenceId de navigation séparés des EvidenceId de corps ;
- les requêtes et lectures déjà tentées, avec rendement et diagnostic ;
- le motif de rejet ou la lacune qui empêche la validation ;
- un indicateur de doublon canonique et les variantes de titre observées.

Le Writer ne démarre que lorsqu'un dossier de couverture lui est remis. Pour le
stress-test 5 x 4, la cible initiale proposée est sept corps vérifiés par colonne
pour cinq cases, soit cinq candidats et deux réserves. Le modèle reste libre de
réaffecter un candidat ; le code vérifie seulement le compte, les identités,
l'existence des corps, la visibilité des preuves et les plafonds.

## Boucle de recherche proposée

1. Le Planner décompose la demande en rôles à couvrir et produit des recherches
   de découverte larges.
2. L'Explorer classe les retours : corps directement exploitable, entrée de
   sommaire à suivre, doublon, bruit ou candidat rejeté.
3. Pour chaque titre prometteur, il exécute une recherche littérale dans la
   source puis une lecture canonique des pages physiques valides. Les numéros
   imprimés du sommaire ne sont jamais supposés être des coordonnées physiques.
4. Après chaque tour, il sauvegarde l'inventaire compact et calcule les lacunes
   mécaniques : rôles sans corps, candidats sans titre exact, preuves évincées,
   réserves manquantes et opérations sans rendement.
5. La recherche continue sur les lacunes. Elle s'arrête sur couverture, plafond
   explicite ou absence réellement établie. Une limite de calcul est rapportée
   comme telle et ne devient pas une insuffisance documentaire.
6. Le Writer reçoit uniquement les corps vérifiés et l'inventaire. Le Critic
   contrôle ensuite adéquation, diversité et maintien des preuves de corps.

Cette architecture répond directement au défaut A855 : les titres découverts
sur un sommaire survivent au changement de focus, et le système sait quels
titres ont déjà été suivis jusqu'au corps. Elle réduit aussi le coût, car un
candidat validé n'est pas renvoyé intégralement à chaque tour de découverte.

## Travail réalisable sans nouvelle dépense API

La première implémentation peut être validée hors ligne :

1. définir les contrats et transitions de l'inventaire ;
2. rejouer les complétions capturées A849, A855 et A856 sans réseau ;
3. utiliser A813/A817 comme oracle connu de titres et de corps, sans encoder ces
   recettes dans la logique de production ;
4. vérifier les quotas, la déduplication, la séparation navigation/corps, la
   compaction et la reprise après interruption ;
5. prouver qu'un corps déjà sélectionné ne peut plus être évincé par un sommaire
   ou par le changement de colonne ;
6. exécuter la suite backend et les contrôles PostgreSQL ciblés si le gateway ou
   la persistance changent.

Ces travaux doivent précéder toute nouvelle location de GPU. Ils permettent de
tester l'architecture sans payer un modèle pour redécouvrir les mêmes défauts.

## Validation live suivante

Quand un calcul avancé redevient disponible, l'ordre proposé est :

1. un seul pilote connu du planning sur état figé avec l'inventaire activé ;
2. audit des vingt affectations, des vingt corps et des cartes physiques ;
3. deux répétitions supplémentaires uniquement après réussite du premier, pour
   atteindre 3/3 end-to-end ;
4. un holdout aveugle nouveau pour détecter le sur-ajustement au corpus cuisine ;
5. validation du basculement local vers avancé et du rendu WinUI ;
6. mesures de latence et de coût sur l'infrastructure serveur candidate.

OpenAI a servi de modèle fonctionnel temporaire. Le provider est déjà abstrait
par endpoint compatible ; la prochaine campagne peut donc utiliser RunPod, un
serveur loué ou le futur serveur client sans modifier le contrat documentaire.
Le choix doit comparer coût horaire, mémoire GPU, contexte utile, concurrence,
latence et qualité. Il ne faut pas réintroduire des branches de logique propres
à chaque hébergeur.

## Relation avec le petit modèle et les licences

Le petit modèle local conserve les demandes directes dont la frontière a été
mesurée, ainsi que la clarification et le routage. Les tâches longues,
multisources ou à forte couverture passent à la capacité avancée. Le lieu du
grand modèle reste une configuration : serveur client, serveur SAAIA, RunPod,
Azure ou endpoint compatible autorisé.

L'installateur et la licence devront plus tard matérialiser cette matrice de
capacités, les endpoints, les secrets, le téléchargement du modèle local et les
préflight matériels. Ce chantier reste différé, mais le nouvel inventaire ne
doit dépendre ni d'un fournisseur ni de la localisation du modèle.

## Critères de sortie de la prochaine phase

La phase A857 sera prête pour un pilote live lorsque :

- l'inventaire est typé, borné, sérialisable et reprenable ;
- chaque candidat distingue découverte et corps vérifié ;
- la couverture et les lacunes sont calculées sans jugement métier codé ;
- les replays A849/A855 démontrent la conservation des candidats ;
- A817 peut être transmis au Writer sans reconstruction manuelle ;
- les suites déterministes sont vertes et les chemins ordinaires restent hors
  réseau.

Le planning, la capacité avancée et le produit restent `TESTE_NON_APPROUVE`
jusqu'aux validations live end-to-end, au holdout et à WinUI.
