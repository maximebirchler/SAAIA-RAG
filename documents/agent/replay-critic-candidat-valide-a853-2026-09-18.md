# A853 — Critic A852 sur le candidat Writer A817 validé

## Pourquoi ce replay

A852 a correctement rejeté le candidat A849 : charlotte au petit-déjeuner,
coulis seul en collation et absence de remplaçants suffisants dans les 53 preuves.
Ce refus valide le garde sémantique, mais ne prouve pas que le Critic sait
préserver un bon planning.

Le diagnostic A817 fournit un contrepoint déjà audité : vingt choix distincts,
vingt liaisons sur vingt acceptées, vingt-trois preuves canoniques et une revue
sémantique `PASS_KNOWN_WRITER_SYNTHESIS_WITH_NATIVE_CONTENT`. Il contient des
petits-déjeuners, déjeuners, collations et soupers ordinaires, sans charlotte au
petit-déjeuner ni coulis autonome. Le Writer avait réussi en un appel avec des
lectures choisies extérieurement ; aucun Critic n'avait été exécuté.

A853 rejoue ce candidat et uniquement ses vingt-trois preuves dans le Critic
A852. Cela mesure si le nouveau garde conserve une bonne synthèse au lieu de
surcorriger ou de fabriquer une insuffisance.

## Protocole préenregistré

- fixture privée produite par `tools/prepare-native-writer-critic-replay.py` à
  partir des artefacts clos A813/A817 ;
- exactement les vingt-trois preuves citées, aucune clé, aucun en-tête
  d'autorisation, aucun raisonnement chiffré ;
- Planner et Writer synthétiques à coût nul ;
- un seul appel live Critic, `researchAllowed=false`, raisonnement `high` ;
- registre avant A853 : 39,783276 USD ; solde calculé : 0,216724 USD ;
- plafond A853 et plafond cumulé fixés au solde acheté restant, sans possibilité
  de dépasser 40 USD ;
- aucune recharge et aucun achat ; produit `TESTE_NON_APPROUVE`.

Le succès attendu est un résultat `answered`, vingt choix distincts, vingt
cellules, preuves de corps conservées et aucun nouveau placement inadapté. Une
insuffisance ou une correction injustifiée est un rejet. Même un succès reste un
diagnostic connu : les lectures A817 ont été choisies extérieurement et ne
valident pas encore la navigation autonome end-to-end.
