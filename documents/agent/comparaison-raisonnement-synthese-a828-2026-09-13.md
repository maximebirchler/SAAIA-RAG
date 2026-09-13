# Niveau de raisonnement de synthèse — A828, préenregistrement

## Cause encore à départager

A827 sur `503117e6` : l'espace de travail est offert et techniquement valide,
mais Terra ne l'utilise pas. Il choisit des recherches générales puis une
insuffisance avant la limite. Six appels coûtent 0,3402479 USD. Aucune lecture,
aucune recherche littérale, aucune grille finale. Pas de répétition aveugle.
Rendre des outils disponibles et redistribuer les tours n'a pas suffi.

Le raisonnement commun est low. Le Planner a une limite de 512 tokens ; la
synthèse et la critique, 4 096. Changer le raisonnement global modifierait
également le Planner sous cette petite limite. Isoler ici le raisonnement de
la boucle de synthèse et de critique ; le Planner, les réparations et la
récupération conservent low. Aucun plafond de tokens augmenté.

## Intervention unique

Option `SynthesisReasoningEffort`, vide par défaut, héritant alors du réglage
commun. Pour ce pilote OpenAI DEV, medium uniquement dans la synthèse et la
critique, y compris leurs retours de recherche. Le transport Responses
reporte les items de raisonnement comme avant. Le protocole et le modèle
restent inchangés ; aucun basculement implicite, outil imposé ou aide métier.
Le transport interne actuel ne reçoit pas de paramètre OpenAI ajouté.

La validation refuse une valeur invalide avant HTTP. Vérifier les valeurs
réellement envoyées : Planner low, Writer medium, retours medium et Critic
medium, ainsi que l'héritage lorsque l'option est vide et le chemin interne.

## Pilote et arrêt

Même cas public, corpus et runtime figés ; topologie agent, workspace activé
comme A827, mêmes passages et outils, sept appels, HTTP une tentative,
Writer/Critic 4 096 tokens. Un pilote au plus 0,75 USD avant analyse.
Mesurer les opérations choisies, l'usage de mémoire, la visibilité, les
associations finales, la durée, les tokens et le coût. Une sortie coupée est
rejetée ; ne pas réduire discrètement le raisonnement ou accepter son fragment.
Si cette limite se manifeste, documenter le rôle exact avant tout autre essai.

Arrêt au premier échec sans hypothèse nouvelle. Si succès, figer et exiger
trois réussites consécutives avant questions inédites et WinUI. Cela ne
démontre ni la limite maximale de Terra ni les performances du futur serveur.
Le produit reste `TESTE_NON_APPROUVE`.

Registre clos : 32,68584560 USD sur 40 USD, reste 7,31415440 USD.
Aucun nouvel achat ni location.

## Contrôle avant pilote

L'option est implémentée. Quatre nouveaux cas vérifient les requêtes réellement
envoyées pour l'héritage low, l'override medium dans Writer/retour/Critic, le
transport interne sans paramètre OpenAI et le refus avant I/O d'une valeur
invalide. Les réparations et la récupération n'activent pas cet override.
Suite backend Release : 2 371 réussites, aucun échec, trois ignorés ; build
sans avertissement et diff propre. Aucun résultat réel A828 encore acquis.

## Résultat réel clos

Sur `d65f240e`, six appels coûtent 0,3434699 USD. Le medium est confirmé
dans les enveloppes API. Writer s'arrête après un tour ; Critic choisit trois
recherches supplémentaires, puis revient en insuffisance. Quinze recherches
au total, aucune lecture, recherche littérale ou sauvegarde de mémoire.
La récupération produit une assertion agrégée de treize préparations avec
treize références. Treize cartes passent l'audit physique. Cela n'approuve
ni treize préparations complètes ni un déficit minimum de sept dans le corpus.
Grille absente : rejet, aucune nouvelle répétition. Ressources fermées.
Registre 33,02931550 USD sur 40 USD. Contrat court A830 préenregistré séparément.
