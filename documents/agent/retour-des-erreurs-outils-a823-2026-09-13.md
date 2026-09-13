# Retour des erreurs de lecture au modèle — A823

## Résultats qui motivent la correction

Le pilote A820, candidat `c4a92279`, a livré un planning autonome de vingt
propositions distinctes : seize préparations documentées et quatre fruits
proposés tels quels comme collations. La demande ne réclamait pas vingt recettes.
Les fruits sont attestés dans une liste saisonnière ; leur affectation est une
proposition du modèle, pas une prescription du document. Ce résultat ne prouve
donc pas la capacité à fournir vingt recettes. Les vingt associations réellement
visibles ont été relues et les dix-sept cartes uniques ont passé le contrôle
physique fichier/hash/révision/page/ancre. Six appels, 0,3304172 USD.

La deuxième répétition A821 sur le même état figé a échoué après quatre appels
facturés, 0,1577980 USD. Le Writer a demandé notamment les pages 34 à 38 incluses,
soit cinq pages, alors que le contrat autorise quatre pages. Les sources étaient
observées et autorisées. Le parseur a rejeté atomiquement le lot de trois lectures
et cinq recherches avant exécution. Les seize événements antérieurs sont des
recherches réussies ; aucune lecture de ce lot n'a été exécutée. Le modèle n'a
pas reçu le motif de rejet. Ce diagnostic concerne les arguments du contrat,
pas une défaillance PostgreSQL, un hash incorrect ou une absence de recettes.

Verdict : un succès connu ponctuel, puis échec technique. Troisième répétition
non démarrée ; stabilité trois sur trois non établie. Aucun nouveau holdout ou
parcours WinUI de ce planning n'est approuvé. `TESTE_NON_APPROUVE` est conservé.

## Comportement corrigé

Une fenêtre de pages invalide sur une source déjà observée produit un retour
typé : opération, source opaque, bornes demandées et maximum inclusif. Le lot
rejeté reste entièrement inexécuté. Le modèle peut soumettre un nouveau lot
complet corrigé, ou une réponse finale étayée. Le code ne découpe pas les pages
et ne choisit pas de nouvelles sources à sa place.

Ce retour est disponible pour le ResearchReview et pour la synthèse Writer ou
Critic. Une seule correction d'arguments par boucle est permise et elle consomme
les appels déjà autorisés. Les appels réservés à la réponse finale restent
protégés. Pas d'appel supplémentaire si le budget ne permet pas cette correction.
Une nouvelle demande invalide termine avec l'erreur de protocole existante.

Une source inconnue, une portée documentaire incohérente ou un autre protocole
invalide conservent le rejet strict. La correction ne donne aucun accès nouveau.
Les limites d'outils, de quatre pages, de contexte, de temps, de tentatives HTTP
et de coût restent identiques. Aucun nom de recette ni règle métier n'est ajouté.

## Vérification et qualification suivante

Cinq nouveaux cas automatisés couvrent la correction en préparation, la
synthèse avec ou sans critique, le rejet répété et le manque de budget. La suite
backend Release passe : 2 318 réussites, zéro échec, trois ignorés. Les requêtes
invalides ne sont pas exécutées et les bornes corrigées sont celles du modèle.
`git diff --check` est propre. Ces transports simulés prouvent le mécanisme,
pas encore son utilisation correcte par Terra dans une exécution réelle.

La nouvelle version doit être figée avant un pilote réel connu, au plus sept
appels, une tentative HTTP et 0,75 USD. Si elle passe, trois succès consécutifs
sur cette nouvelle version seront nécessaires ; le succès historique A820
ne compte pas pour cette nouvelle série. Arrêter la série au premier échec.
Le holdout inédit et WinUI viennent ensuite, sans consultation anticipée.

Après A821, le registre clos totalise 31,23417630 USD sur les 40 USD achetés
et autorisés, soit 8,76582370 USD restants. Aucun nouvel achat n'est effectué.
Les preuves privées restent dans les artefacts locaux : évaluations A820,
audit des événements A821, arrêt de qualification A821 et suite A823.
