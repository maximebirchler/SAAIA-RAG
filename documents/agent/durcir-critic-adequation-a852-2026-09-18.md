# A852 — Adéquation fonctionnelle et preuves du Critic

## Constat causal A851 R2

Le replay R2 a atteint Terra avec les 53 preuves et le candidat A849. Le premier
appel Critic a demandé de lui-même une recherche ciblée sur des barres de
céréales. Le second a rendu vingt cellules, mais a conservé `Charlotte` au
petit-déjeuner et `Coulis de framboises` comme collation. Il a aussi remplacé les
preuves de corps de `Poêlée de pâtes`, `Poêlée au riz` et `Brochettes de volaille
à la pêche` par un index seul. Le garde SAAIA a donc refusé la publication avec
`advanced_synthesis_candidate_body_not_supported`.

R2 a consommé 0,194754 USD : 0,073104 USD pour la demande de recherche et
0,121650 USD pour le résultat terminal. Le Critic a prouvé qu'il sait employer
un outil, mais pas qu'il sait corriger l'adéquation des créneaux. Le résultat
reste un échec sémantique et documentaire.

## Intervention A852

La modification porte uniquement sur les contrats Critic des deux styles de
prompt. Elle rend explicites deux obligations déjà attendues :

1. distinguer l'identité documentée de l'adéquation fonctionnelle au créneau ;
2. conserver au moins une preuve de corps pour chaque choix nommé lorsque cette
   preuve est visible.

Pour un planning de repas, le Critic reçoit des contre-exemples génériques : une
sauce, un coulis, un condiment, une garniture ou un accompagnement ne constitue
pas seul un repas ou une collation sans preuve contraire ; un dessert ne devient
pas un petit-déjeuner parce qu'il est comestible. Il doit d'abord réaffecter les
candidats distincts déjà supportés. Une recherche de remplacement infructueuse
ne l'autorise pas à répéter le placement incompatible.

Ces règles restent conditionnelles au type de tâche. Elles ne codent aucune
recette ni aucun résultat de planning et ne sélectionnent pas de remplacement à
la place du modèle.

## Replay préenregistré

- candidat et 53 preuves identiques à A851 ;
- Planner et Writer synthétiques à coût nul ;
- un seul appel live Critic ;
- `researchAllowed=false` par limite de trois appels fournisseur, afin de mesurer
  la capacité de réaffectation sur les preuves déjà disponibles ;
- Critic Terra, raisonnement `high`, 8 192 jetons de sortie maximum ;
- registre avant A852 : 39,702512 USD sur 40 USD achetés ;
- plafond A852 : 0,18 USD, plafond cumulé 39,882512 USD ;
- aucune recharge, aucun achat, aucune publication automatique ;
- produit maintenu `TESTE_NON_APPROUVE`.

Le succès mécanique exige un résultat accepté par tous les gardes. Le verdict
sémantique vérifie séparément les vingt créneaux, la correction des trois cas
discutables, les preuves de corps et l'absence d'invention. Un seul résultat
accepté ne suffit pas à approuver le produit.
