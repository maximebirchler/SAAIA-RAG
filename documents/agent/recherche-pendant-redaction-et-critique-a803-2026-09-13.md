# A803 — Rechercher pendant la rédaction et la critique

État : TESTE_NON_APPROUVE. Ce lot corrige un mécanisme ; il ne valide pas encore
la qualité du planning ni la capacité avancée du produit.

## Défaut constaté et changement

Le job réel A802 a laissé une des vingt cases non documentée. Sa rédaction et
sa critique pouvaient reconnaître une preuve insuffisante, mais leur protocole
n'offrait plus de retour vers la recherche du corpus. La recherche adaptative
était disponible avant la rédaction seulement.

Writer et Critic peuvent maintenant demander `research_required` avec des
requêtes ciblées ou reformulées et, si nécessaire, le `sourceKey` opaque d'une
source déjà observée. SAAIA exécute ces requêtes via la même passerelle du
corpus, revalide les preuves et rappelle la même phase avec les observations
actualisées. Seul le résultat terminal est publié. Le fournisseur, le modèle,
le tenant, le job et les contrôles budgétaires restent ceux du job initial.

Le modèle reçoit les catégories, les recherches déjà exécutées, le nombre
d'appels restant pour cette phase et la disponibilité effective de l'outil.
Les plafonds du job bornent la boucle ; une réserve permet la critique après
la rédaction. Une demande vide, un handle inconnu, une répétition sans progrès
ou une demande dépassant les appels disponibles produit une erreur typée.
Atteindre cette limite ne constitue pas une preuve d'absence documentaire.

L'historique des recherches expose les handles opaques, sans transmettre les
identifiants ni les chemins privés utilisés en interne pour cibler une source.
Ce point corrige aussi une fuite potentielle introduite par les recherches
scopées du lot A802 lors de la deuxième revue de recherche.

## Vérification effectuée

Six contrôles comportementaux échouaient avant la correction puis passent :
recherche tardive depuis Writer et Critic, preuve de corps de document reprise
dans la réponse, refus du scope inconnu, absence de progrès, limite d'appels et
absence de fuite dans une deuxième revue. Trois contrôles supplémentaires
refusent des actions vides sans créer une recherche implicite de la question.

Suite backend Release : 2 241 réussites, zéro échec, trois tests live ignorés.
Preuve :
`artifacts/reprise-pc-20260908/a800-meal-planning-continuity-20260913/backend-late-research-final.trx`.
Ces tests utilisent des réponses simulées pour isoler le mécanisme : aucun
crédit OpenAI n'est consommé et aucune approbation sémantique n'en découle.

## Prochaine preuve

Rejouer une fois le planning connu sur ce code figé et examiner les demandes de
recherche effectivement produites, les vingt choix et leurs liens exacts vers
les preuves. Ce diagnostic connu ne remplace pas les répétitions ni une banque
aveugle future. Aucun nouveau corpus de questions aveugles n'a été généré.

Le budget reste limité aux 30 USD déjà achetés. Aucun achat, déploiement,
installateur ou essai RunPod n'est inclus dans ce lot.
