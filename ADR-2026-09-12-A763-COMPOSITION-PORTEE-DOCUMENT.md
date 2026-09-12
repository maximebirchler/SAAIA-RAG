# A763 — Composer une preuve de portée et une preuve d'item

Date : 2026-09-12  
Statut : correction partielle ; probe Terra ciblé rejeté

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

## Probe Terra ciblé

La campagne préenregistrée `A763-TERRA-SOURCE-SCOPE-STUDENT-3X`, exécutée sur
le commit propre `239ab92b`, a terminé trois répétitions sans erreur de quota ni
d'infrastructure. Le sceau du corpus est identique avant et après. Six appels
Terra ont coûté 0,079726 USD.

Le résultat ciblé est rejeté : deux répétitions donnent l'insuffisance sûre à
quatre repas et une seule donne cinq items. Cette dernière emploie une recette
d'un autre document sans démontrer le qualificatif étudiant et reproduit la clé
interne `S4` dans le texte utilisateur. Le verdict mécanique générique reste
`PASS_MECHANICAL_REQUIRES_SEMANTIC_REVIEW`, mais les critères préenregistrés du
probe ne passent pas.

Le protocole utilise maintenant le préfixe reconnaissable
`internal-source-N`, interdit explicitement dans les textes publiés. Une sortie
qui le reproduit est rejetée puis soumise à l'unique réparation bornée, laquelle
doit le retirer sans inventer un nom de source.

La prochaine étape causale consiste à vérifier l'ordre exact des preuves
transmises au Writer. Les chunks canoniques du livre étudiant contiennent un
index et plusieurs recettes nommées ; le problème restant est de garantir que
la preuve de portée et suffisamment de preuves d'items du même document entrent
ensemble dans le budget de contexte.

L'inspection des dix-huit événements de recherche du probe confirme ce point.
Dans chaque répétition, le livre étudiant remonte sur la plupart des six
requêtes et expose les pages d'index 3 et 4. Les doublons de couverture des
pages 1 et 22 passent cependant avant elles dans l'ordre plat ; le budget du
Writer peut être consommé avant les preuves d'items.

Pour une collection `multi_item`, le Writer reçoit désormais en tête les pages
distinctes de la source qui cumule la plus grande couverture des requêtes. La
promotion est bornée au nombre d'items demandé plus un, puis l'ordre global
reprend sans supprimer aucune autre preuve. Elle ne s'applique ni aux grilles,
ni aux comparaisons, ni aux extractions bornées d'un document nommé. Cette
sélection reste générique et ne change pas le moteur de recherche canonique.
