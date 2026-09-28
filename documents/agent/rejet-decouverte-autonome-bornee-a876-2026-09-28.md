# A876 — chaîne technique complète, découverte autonome encore insuffisante

## Verdict

A876 franchit les défauts de contrat rencontrés par A868 à A875. Le Planner,
le Candidate Explorer, le Writer et le Critic terminent. Le résultat mécanique
est `PASS_MECHANICAL_REQUIRES_SEMANTIC_REVIEW` et l'audit privé conclut
`PASS_PRIVATE_EVIDENCE_INTEGRITY_REQUIRES_SEMANTIC_REVIEW`. Le Writer rend une
insuffisance bornée sans créer de claims fictifs, puis le Critic la conserve.

Le pilote fonctionnel reste toutefois rejeté. Le dossier final ne contient que
treize choix affectés aux rôles : deux petits-déjeuners, deux déjeuners, quatre
collations et cinq soupers. Il manque donc trois petits-déjeuners, trois
déjeuners et une collation pour produire la grille 5 × 4. Les diagnostics A813
et A817 ont déjà établi des corps documentaires permettant vingt propositions.
Cette insuffisance décrit la recherche bornée d'A876 ; elle ne démontre pas une
absence du corpus.

## Ce que Terra a réellement reçu et fait

Terra disposait des extraits documentaires, des métadonnées canoniques, de
`search_corpus`, `find_source_text`, `read_source`, du workspace persistant et
de l'inventaire de candidats. Il a utilisé les neuf appels autorisés : Planner,
six tours Explorer, Writer et Critic. Les six tours Explorer ont produit dix-huit
opérations documentaires : huit recherches initiales, quatre recherches
sémantiques restreintes à une source, trois lectures de pages physiques puis
trois autres recherches restreintes à une source.

Le modèle a donc raisonné, changé de sources et lu des corps. Le problème n'est
pas une absence d'outils. Malgré des sommaires et index visibles, il est revenu
de façon probabiliste à des requêtes sémantiques générales dans les mêmes
sources au lieu de suivre systématiquement des noms d'entrées avec
`find_source_text`. A874 avait montré que ce chemin exact fonctionne lorsqu'il
est choisi. Un appel A876 a aussi été consommé pour corriger une lecture de cinq
pages, alors que la fenêtre maximale autorisée est de quatre ; le retour outil
a permis au modèle de reprendre avec une plage valide.

## Dossier obtenu

Les treize candidats affectés et vérifiés sont :

- petit-déjeuner : `PANCAKES`, `PORRIDGE AUX FLOCONS D’AVOINE` ;
- déjeuner : `SALADE CROQUANTE ET RILLETTES DE SARDINES`,
  `SANDWICH COMPLET ET ÉQUILIBRÉ SELON VOS ENVIES` ;
- collation : `LES BISCUITS À MIKE`, `Compote à la rhubarbe et aux fraises`,
  `MUFFINS AUX POMMES`, `SCONES AUX CANNEBERGES` ;
- souper : `Ragoût de poisson espagnol`, `CURRY DE CREVETTES`,
  `SOUPE DE COURGETTES CHÈVRE LARDONS`, `SOUPE AU CHOUX`,
  `COLOMBO DE POISSON`.

Le checkpoint privé contient quarante-trois candidats, dont quarante avec un
corps vérifié. Seuls treize sont toutefois affectés par le modèle à un rôle du
planning. Les rubriques automatiques restantes ne peuvent pas être comptées
mécaniquement à la place de son jugement sémantique.

## Correction causale suivante

Le prompt du Candidate Explorer explicite maintenant l'arbitrage déjà attendu : lorsqu'un
sommaire ou un index visible contient des entrées nommées prometteuses pour un
besoin non couvert, le modèle privilégie `find_source_text` avec le nom exact,
ou `read_source` avec des coordonnées observées, avant de relancer une recherche
sémantique large dans la même source. Le modèle conserve la décision sur la
pertinence de l'entrée et doit toujours vérifier son corps avant acceptation.
Cette règle est documentaire et générale ; elle ne contient aucun titre de
recette, domaine, langue ou réponse attendue.

## Coût et preuves

A876 a consommé neuf appels, 0,61069820 USD et environ 86 secondes. Le registre
global contient 1 172 écritures, totalise 42,90447660 USD et laisse
2,09552340 USD jusqu'au plafond autorisé de 45 USD. Son SHA-256 est
`3BA4D7DB9874CE02EB0B63F5459C19521192720BE809B1FCEB7F3A29C62050B2`.

Le corpus avant/après conserve 305 documents, 31 catégories, 120 130 points et
le sceau composite
`7B119CDB072F909236CF3291790B0E287ECCEF48716FC97E338CE8C7EFB798E2`.
Les preuves privées sont conservées sous
`artifacts/reprise-pc-20260908/a876-terra-candidate-explorer-pilot-20260928-170823/`.
Le produit reste `TESTE_NON_APPROUVE`.
