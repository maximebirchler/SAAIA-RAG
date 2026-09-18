# A851–A856 — bilan de la campagne Terra finale

## Verdict

La campagne isole maintenant deux comportements différents.

1. Le Writer et le Critic Terra savent produire et conserver un planning de
   vingt cases lorsque le dossier de candidats contient les bonnes recettes et
   les bonnes preuves de corps. Le replay connu A817 passe trois fois sur trois.
2. La navigation autonome ne constitue pas encore ce dossier de façon fiable.
   Le dernier pilote ciblé trouve trois petits-déjeuners bien prouvés, puis
   déclare une insuffisance alors que les preuves historiques A813/A817 montrent
   d'autres candidats disponibles dans le même corpus.

Le goulot actuel est donc la découverte, la qualification et la conservation
des candidats avant synthèse. Il n'est plus justifié d'ajouter des consignes au
Writer final tant que cette frontière n'est pas restructurée.

## Résultats observés

| Contrôle | Appels payants | Coût USD | Résultat sémantique |
|---|---:|---:|---|
| A851-R1 | 1 | 0,0877580 | Le replay transporte mal les 53 preuves ; une correction Writer est appelée avant Critic et rend une insuffisance. Non probant pour le Critic. |
| A851-R2 | 2 | 0,1947540 | Le Critic demande une recherche puis conserve `Charlotte` au petit-déjeuner et `Coulis de framboises` comme collation. Il perd aussi plusieurs preuves de corps. Rejet. |
| A852 | 1 | 0,0807640 | Après durcissement généraliste, le Critic refuse explicitement ces deux placements et n'invente pas de remplacements. Refus sûr, mais aucune grille. |
| A853 | 1 | 0,0635206 | Candidat A817 valide : `answered`, 20 claims, 20 choix distincts et 23 preuves de corps. |
| A854-R2 | 1 | 0,0303344 | Même candidat : réussite identique, cache actif. |
| A854-R3 | 1 | 0,0307064 | Même candidat : réussite identique. Le contrôle connu atteint 3/3. |
| A855 | 3 | 0,0626602 | Éclaireur petit-déjeuner : trois candidats correctement prouvés, puis insuffisance prématurée. |
| A856 | 3 | 0,0238630 | Trois sondes Planner, volontairement arrêtées avant Writer. Les requêtes sont conservées ; aucune réponse n'est validable. |

La réconciliation de départ a aligné le registre local à 39,42000000 USD sur la
dépense d'organisation alors visible. Après A851–A856, il atteint
39,99436060 USD sur le plafond autorisé de 40 USD. Le reliquat est
0,00563940 USD. Un contrôle final avec ce plafond reçoit
`advanced_external_budget_job_cost_limit` avant réseau ; le registre conserve
1 127 lignes et le SHA-256
`79665AC09BE37F2D83FC7D455391F8CB13781E1BD7769EC18245CF5C8CB4D0CC`.
Le budget est donc épuisé opérationnellement sans dépassement et sans recharge.

## Ce que le 3/3 démontre

La fixture A853 contient le candidat A817 déjà audité, vingt claims et les
vingt-trois preuves de corps effectivement citées. Les trois résultats A853,
A854-R2 et A854-R3 sont `answered`. Ils conservent le même ordre de vingt choix,
vingt identités distinctes et exactement le même ensemble de preuves. Les coûts
sont respectivement 0,0635206, 0,0303344 et 0,0307064 USD. L'artefact
`meal-a854-known-candidate-repeatability-assessment.v1.json` porte le verdict
`PASS_KNOWN_CANDIDATE_CRITIC_3_OF_3`.

Cette preuve est causale mais étroite : elle valide le Critic sur un candidat
connu. Elle ne valide ni la navigation autonome, ni le trajet produit complet,
ni WinUI. Elle ne remplace pas les trois réussites end-to-end exigées par le
Goal.

## Ce que le pilote A855 démontre

Le Planner A855 a proposé six recherches raisonnables sur le petit-déjeuner.
Le Writer a ensuite demandé quatre recherches supplémentaires, dont deux dans
des sources précises. Le job a finalement retenu :

- `PORRIDGE AUX FLOCONS D'AVOINE`, avec un corps qui le qualifie de
  petit-déjeuner ;
- `PANCAKES`, avec préparation et mention explicite du petit-déjeuner ;
- `SCONES AUX CANNEBERGES`, avec mention petit-déjeuner ou brunch.

Le refus d'inventer quatre autres recettes est correct. Son explication selon
laquelle les extraits revalidés n'en documentent que trois ne décrit toutefois
pas une insuffisance du corpus : A813/A817 avait déjà extrait des corps valides
pour `FAJITAS DÉJEUNER À JOSIANE`, `FRITTATA À FLO`, `LA CRÊPE À JO` et
`MUFFINS DE BASE`. A855 a épuisé ses trois appels après un seul tour natif ; il
n'a pas construit ni repris un inventaire assez large. C'est une insuffisance de
recherche disponible pour ce job, pas une preuve d'absence documentaire.

## Ce que les sondes A856 démontrent

Les Planners comprennent le rôle des sommaires et proposent explicitement des
requêtes de navigation :

- déjeuner : `sommaire recettes déjeuner`, `table des matières recettes
  salades sandwichs soupes tartes quiches`, puis des familles précises ;
- collation : `sommaire recettes collation goûter encas`, `table des matières
  index recettes snack`, puis barres, biscuits, muffins et bouchées ;
- souper : `table des matières recettes plats principaux`, `sommaire recettes
  souper dîner`, ainsi que des variantes anglaises et des familles de plats.

Ils ne sont donc pas incapables de reconnaître qu'un livre ou son sommaire peut
servir de carte. La limite apparaît ensuite : une liste de requêtes générales
ne devient pas automatiquement une collection persistante de titres exacts,
de corps relus et de rôles couverts. Les trois jobs ont été arrêtés, comme
préenregistré, avant Writer avec
`advanced_synthesis_research_call_budget_exhausted`. Le banc global les signale
donc en échec de contrat, ce qui est attendu pour des sondes et ne constitue pas
une réponse produit.

## Changements conservés

- le Critic distingue désormais identité documentaire et adéquation au rôle ;
- une sauce, un coulis, un condiment ou un accompagnement ne peut pas être gardé
  comme élément autonome uniquement parce qu'il est documenté ;
- un placement inadéquat doit être réaffecté ou remplacé avant d'être conservé ;
- les preuves de corps ne doivent pas être remplacées par un sommaire ou un
  index dans la correction finale ;
- le replay payant est explicitement activé et ses plafonds Writer/Critic sont
  fournis par l'environnement ; les suites ordinaires restent hors réseau ;
- le registre de coûts, les traces privées, les identités de jobs et les
  artefacts de résultat permettent de distinguer appels synthétiques et appels
  Terra réels.

La suite backend finale compte 2 437 réussites, zéro échec et trois tests live
ignorés. `git diff --check` passe également. Les trois tests live restent
désactivés dans la suite ordinaire et aucun appel externe n'a été déclenché par
ce contrôle final.

## Statut produit

Le résultat connu du Critic est validé. La recherche autonome et le planning
5 x 4 end-to-end ne le sont pas. Aucun contrôle WinUI terminal n'a été exécuté
sur cette nouvelle chaîne. Le statut reste `TESTE_NON_APPROUVE` et le Goal reste
actif.
