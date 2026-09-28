# A882 — préenregistrement du premier pilote live staged

## Décision

Le premier pilote Terra du pipeline staged est entièrement défini, câblé dans
les runners et contrôlé par un préflight exécutable. Il n'a pas été lancé. Le
préflight refuse actuellement l'exécution parce que le reliquat de
0,57468470 USD ne couvre pas l'enveloppe complète du job fixée à 1,10 USD.

Le profil est
`config/openai-terra-staged-candidate-pilot.a882.json`, SHA-256
`3EEFC2D46F227C7415ED854A7D364AB7E4B181C3E911020B753ADADCF5ABA7E3`.
Il est préenregistré contre
`abffd0681f24559534695ca3fd303d35d65d5128` et reste
`PREREGISTERED_BLOCKED_INSUFFICIENT_FULL_JOB_RESERVATION`.

## Chaîne réellement câblée

Le drapeau staged traverse maintenant les trois niveaux du runner :

1. le profil lit `stagedCandidateExplorerEnabled` ;
2. le wrapper OpenAI transmet `EnableStagedCandidateExplorer` ;
3. le runner provider écrit `StagedCandidateExplorerEnabled` dans la
   configuration temporaire du backend et dans son sceau de préflight.

Le commit `d6d6aced` contient ce transport. Il évite qu'un profil annonce un
pilote staged tout en lançant silencieusement l'ancien Explorer intégré.

## Preuve obligatoire de topologie

Le vérificateur privé accepte maintenant `-RequireStagedPipeline`. Dans ce
mode, il exige dans les traces authentifiées au moins :

- un rôle `candidate-navigator-N` ;
- un rôle `candidate-judge-N` ;
- le Writer ;
- le Critic lorsque sa présence est requise.

L'absence du Navigator ou du Judge rejette l'intégrité de la campagne. Les
huit scénarios du vérificateur passent : un parcours intégré valide, un staged
valide, quatre altérations historiques rejetées, puis absence Navigator et
absence Judge rejetées. Une vérification de rétrocompatibilité sur les
artefacts privés A878 passe encore avec l'ancien mode. Commit : `94ba1540`.

Cette preuve contrôle que les phases ont réellement été appelées ; elle ne
juge pas leur qualité sémantique. La revue humaine des décisions reste
obligatoire.

## Réservation complète du budget

Le préflight historique autorisait le lancement dès que le premier appel
pouvait être réservé. Cette condition était trop faible pour un pipeline qui
doit préserver Navigator, Judge, Writer et Critic. Un premier appel abordable
pouvait donc consommer le reliquat avant la synthèse finale.

Le commit `d6238776` ajoute le garde
`lifetime_budget_headroom_below_job_cap`. Le préflight exige désormais que le
headroom global couvre la totalité de `maximumCostPerJobUsd` avant le premier
appel. Il publie aussi `fullJobReservationSatisfied` dans son sceau.

Mesure actuelle :

| Élément | Valeur |
|---|---:|
| coût enregistré | 44,42531530 USD |
| hard stop mission | 45,00000000 USD |
| headroom | 0,57468470 USD |
| enveloppe maximum du pilote | 1,10000000 USD |
| headroom autorisé supplémentaire minimal | 0,52531530 USD |
| réservation complète satisfaite | non |

Le ledger contient 1 193 entrées et son SHA-256 reste
`26B4ADF4FED88975AAEBA523D7E9AA57CB759B87E5025E07002F5CAAB139CEFE`.
Le garde ne modifie pas le compte OpenAI et ne déclenche aucun achat.

## Configuration figée

Le pilote conserve le cas `A755-ADV-01-meal-grid-5x4`, la banque et son hash,
le corpus de référence, Terra avec reasoning `low`, Responses, topologie
`agent`, workspace, historique 32 768 caractères, Planner 512 tokens,
Navigator/Judge 4 096, Writer 4 096 et Critic 4 096. Le maximum reste douze
appels et 1,10 USD pour un seul job. Le budget explicite des preuves est fixé à
64 000 caractères.

Le runner historique affichait 14 000 caractères, mais le provider relève déjà
mécaniquement la limite des grilles structurées : pour 5 × 4 et quatre colonnes,
le plancher atteint 57 600 caractères et reste borné à 64 000. Un replay A817
supplémentaire avec la valeur historique 14 000 garde bien les 23 preuves, les
lots Judge 12+8, un prompt Writer de 49 751 caractères et l'affectation 20/20.
Le commit `abffd068` rend maintenant 64 000 explicite dans le profil, les deux
runners et le sceau de préflight. Il ne dépend donc plus d'un relèvement interne
invisible au moment de relire la campagne.

Le déroulement nominal attendu est Planner, puis plusieurs cycles
Navigator-vers-outils-vers-Judge, suivi du Writer et du Critic. Les appels
finaux restent réservés par le provider. Le profil ne fixe aucun titre, recette,
source ou résultat attendu dans le code produit.

## Critères d'acceptation

Le pilote ne passe que si :

- le corpus scellé est inchangé ;
- les traces prouvent la topologie staged réelle ;
- le Judge traite uniquement des corps bornés et retourne chaque clé ;
- le solveur obtient `maximumAssignableCount = 20` et vingt affectations ;
- le Writer produit vingt choix concrets, distincts et adaptés, chacun lié au
  corps canonique qui lui a été affecté ;
- les jobs, outils, checkpoints, traces, claims et sources sont conservés ;
- une revue humaine confirme les vingt placements.

Une insuffisance bornée reste une sortie sûre mais constitue un échec de ce
pilote fonctionnel, puisque les corps historiques sont connus. Un premier
succès ne promeut pas le produit : il autorise seulement les répétitions deux et
trois sur le même état figé.

## Préflight exécuté sans modèle

Le dry-run du profil confirme : commit minimum présent, staged activé,
Candidate Explorer activé, douze appels, zéro appel externe. Il est bloqué par
les trois raisons attendues dans un contrôle sans observation live du compte :

- `paid_tier_not_observed` ;
- `paid_tier_observation_missing_or_stale` ;
- `lifetime_budget_headroom_below_job_cap`.

Les deux premières seront réévaluées juste avant une éventuelle exécution ; la
troisième est le blocage réel actuel. Le produit reste
`TESTE_NON_APPROUVE` et le mode staged reste désactivé par défaut.
