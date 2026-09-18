# A854 — Répétabilité du Critic sur le candidat A817

A853 a rendu `answered`, vingt cellules, vingt choix distincts et vingt-trois
preuves conservées en un appel Critic. Il a coûté 0,0635206 USD avec 2 651 jetons
de sortie. Le registre est à 39,84679660 USD ; il reste 0,15320340 USD sur les
40 USD achetés.

A854 tente au plus deux répétitions supplémentaires sur la fixture A853 afin de
mesurer un éventuel 3/3 du Critic connu. Le nombre maximal de jetons du Critic
est abaissé à 4 096, supérieur à la sortie A853 observée. Chaque job garde trois
appels au total : Planner et Writer synthétiques à coût nul, puis un seul Critic
live sans recherche. Le plafond cumulé reste 40 USD et le second replay n'est
lancé que si sa réservation maximale tient encore dans le registre après le
premier.

Chaque répétition doit produire `answered`, vingt claims, vingt `selectedItem`
distincts, les vingt coordonnées et aucune liaison rejetée. La revue sémantique
compare ensuite les placements au candidat A817 déjà audité. Aucun succès ne
transforme ce replay connu en preuve de navigation autonome ou d'acceptation
produit. Aucune recharge ni achat n'est autorisé ; `TESTE_NON_APPROUVE` demeure.
