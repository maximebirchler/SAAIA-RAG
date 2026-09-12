# A763 — Composer une preuve de portée et une preuve d'item

Date : 2026-09-12  
Statut : correction locale validée, validation Terra ciblée requise

## Incident observé après le passage en Tier 1

La campagne Terra finale a démarré après observation fraîche du palier Tier 1.
Elle a effectué seize appels fournisseur sans erreur 429. Elle s'est arrêtée à
la deuxième répétition du cas des cinq repas étudiants : quatre idées étaient
prouvées comme simples et destinées aux étudiants, mais Terra a refusé d'en
inventer une cinquième.

La campagne a produit huit jobs durables avant l'arrêt. La revue canonique
diagnostique donne six réponses sémantiquement acceptables et deux rejets :

- le premier planning déclarait à tort les vingt cellules insuffisantes alors
  que des preuves soutenaient au moins deux petits déjeuners ;
- la première liste étudiante ajoutait un macaroni facile sans preuve de son
  adéquation au public étudiant ;
- les deux comparaisons CEN/IEC et les deux extractions NIST sont acceptées ;
- les deux réponses prudentes restantes sont des insuffisances exactes, sans
  pour autant fermer la porte fonctionnelle du planning complet.

Verdict public : `DIAGNOSTIC_ROWS_REJECTED`, six passages et deux rejets,
`approvalEligible=false`, produit `TESTE_NON_APPROUVE`.

## Cause technique

Le corpus canonique contient un livre dont la couverture qualifie la collection
comme simple, accessible et destinée aux étudiants. Des chunks suivants du
même document énumèrent d'autres recettes. Le Writer recevait les textes et les
identifiants de preuve, mais aucune indication lui permettant de savoir que la
couverture et l'index appartenaient à la même révision documentaire.

Il ne pouvait donc pas composer de façon explicite :

1. une preuve de portée qui qualifie la collection ;
2. une preuve d'item qui nomme une recette de cette collection.

Selon les répétitions, il ajoutait un cinquième item provenant d'un autre
document sans le qualificatif étudiant, ou choisissait l'insuffisance sûre.

## Décision

Chaque élément transmis au Writer porte désormais une clé de source opaque,
stable uniquement dans la requête. Deux preuves partagent cette clé lorsqu'elles
proviennent de la même identité canonique document, révision et hash source.
Les identifiants réels, chemins et hashes ne sont pas exposés par cette clé.

Le contrat autorise la composition d'une preuve de portée et d'une preuve
d'item seulement si leurs clés sont identiques et si le texte de portée vise
clairement la collection du document. Le claim doit citer les deux preuves. Une
portée ne peut jamais être transférée entre deux clés différentes.

Cette règle est générique : elle ne contient aucun vocabulaire de recette,
d'étudiant ou de document de benchmark.

## Limite conservée

La correction ne relâche pas la relation sémantique entre une recette et un
créneau de planning. Le corpus observé ne contient toujours pas, dans les
preuves récupérées, cinq candidats explicites pour chacune des quatre catégories
du planning 5 x 4. Une insuffisance exacte reste donc correcte et sûre, mais ne
valide pas la fonction de planning complet.

## Preuves locales

- tests ciblés du fournisseur OpenAI-compatible : 36/36 ;
- test de groupement : deux preuves de la même révision reçoivent la même clé ;
- test d'isolation : une autre source reçoit une clé différente ;
- test de confidentialité : les UUID réels du document et de la révision sont
  absents du payload Writer capturé ;
- aucun appel externe n'a été nécessaire pour ces tests.

La prochaine preuve causale est une campagne Terra ciblée et préenregistrée sur
le cas des cinq repas étudiants, avant toute nouvelle campagne complète.
