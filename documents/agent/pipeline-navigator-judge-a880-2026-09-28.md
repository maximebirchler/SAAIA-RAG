# A880 — pipeline séparé Navigator, exécution mécanique et Candidate Judge

## Verdict du palier

Le second palier architectural décidé après A878 est implémenté comme chemin
produit sélectionnable et validé avec des réponses LLM simulées. La boucle
monolithique Candidate Explorer peut maintenant être remplacée par une boucle
staged qui sépare explicitement navigation, exécution documentaire,
classification des corps et affectation mécanique.

Cette preuve est déterministe et sans réseau. Elle démontre le contrat et son
exécution par le vrai provider, pas encore la qualité de Terra sur ce nouveau
contrat. `StagedCandidateExplorerEnabled` reste donc désactivé par défaut. Le
produit demeure `TESTE_NON_APPROUVE`.

## Chaîne réalisée

La boucle staged suit désormais ces étapes :

1. Les corps source-exacts déjà présents sont placés en priorité dans un lot de
   Candidate Judge.
2. Le Candidate Judge reçoit au plus douze candidats et uniquement leurs corps
   canoniques visibles. Il n'a aucun outil de recherche.
3. Pour chaque clé, il doit décider `body_verified` avec les rôles qu'il juge
   compatibles, ou `rejected` sans lui inventer un rôle.
4. Le solveur A879 recalcule immédiatement l'affectation distincte et le déficit
   par rôle.
5. Si un déficit subsiste, le Navigator reçoit au plus vingt observations
   compactes : un ancrage par source en priorité, puis les sommaires et
   localisateurs, chacun limité à 1 200 caractères.
6. Le Navigator dispose uniquement de `search_corpus`, `find_source_text` et
   `read_source`. Il ne peut ni écrire le registre, ni classer les candidats, ni
   rédiger la réponse.
7. Le backend valide les arguments, exécute les opérations, conserve les
   identités et les preuves, puis remet les nouveaux corps au Candidate Judge.
8. Lorsque le matching est complet, le Writer reçoit le dossier réalisable ;
   sinon il reçoit le déficit borné lorsque l'enveloppe ne permet plus un cycle
   Navigator-vers-Judge complet.

Le mode intégré historique reste disponible pendant la qualification, ce qui
rend le changement réversible. La configuration staged exige le Candidate
Explorer natif et au moins quatre appels sans Critic ou cinq avec Critic. Les
appels Writer et Critic sont réservés avant de lancer une nouvelle navigation,
afin de ne pas consommer le budget dans une lecture que personne ne pourra
ensuite classifier ou présenter.

## Responsabilités sémantiques et mécaniques

Le Navigator décide quelles entrées ou reformulations sont prometteuses. Le
Candidate Judge décide si un corps est autonome, son identité complète et ses
rôles. Le Writer et le Critic gardent la sélection présentée et la qualité
finale.

Le backend limite les lots, résout les sourceKey, valide les fenêtres de quatre
pages, exécute les offsets, empêche les opérations identiques, persiste le
registre, calcule le matching et transporte les EvidenceId. Il ne transforme
pas une requête de retrieval en adéquation métier.

Un `rejected` peut maintenant conserver son corps de décision avec
`targetRoles: []`. Le schéma natif autorise ce tableau vide, tandis que le
parseur continue d'exiger au moins un rôle pour tout état autre que
`rejected`. Cela empêche de forcer un faux rôle uniquement pour mémoriser un
rejet.

## Gestion des erreurs et du rendement

Le pipeline staged :

- refuse un Judge qui omet une clé, ajoute une clé, sélectionne un élément ou
  cite une preuve hors lot ;
- donne au Navigator une correction bornée pour une fenêtre, un scope ou un lot
  invalide ;
- refuse de réexécuter une opération déjà effectuée et autorise une seule
  correction avant de borner la boucle ;
- renvoie explicitement au Navigator un résultat de lecture trop volumineux
  afin qu'il réduise la fenêtre ou augmente un `topK` encore autorisé ;
- priorise les candidats sans rôle dans le contexte du Judge, afin que les
  corps déjà classifiés ne masquent pas les corps encore en attente lorsque le
  contexte est plein ;
- garde les opérations documentaires stateless entre Navigator et Judge : la
  preuve passe par l'EvidenceBundle courant, pas par un historique d'outil pris
  pour une preuve.

## Tests et résultat

Quatre nouveaux scénarios couvrent le chemin staged :

- vingt corps sont jugés en deux lots exacts de douze puis huit avant Writer ;
- un sommaire va au Navigator, `find_source_text` est réellement exécuté par le
  gateway, puis le corps obtenu va au Judge sans outil ;
- un fragment peut être rejeté avec zéro rôle et ne réapparaît pas dans le
  dossier Writer ;
- une activation incohérente ou une enveloppe incapable de préserver un cycle
  complet est refusée avant tout appel.

Le fournisseur passe 311/311. La suite complète finale compte 10 Contracts,
2 475 Backend et 2 338 Client réussis, zéro échec et quatre tests live
explicitement ignorés. `git diff --check` est propre.

Aucun appel OpenAI n'a été effectué. Le coût reste 44,42531530 USD sur le hard
stop local de 45 USD.

## Limites et prochaine porte

Les tests utilisent des réponses Navigator et Judge simulées. Ils prouvent que
le vrai code sépare correctement les responsabilités et que les données remises
à chaque phase sont bornées, mais ils ne prouvent pas encore que Terra suit le
contrat, choisit de bonnes entrées ou classe correctement les vingt corps.

La prochaine porte est un replay privé local du dossier A817 à travers la
projection staged, sans appel réseau et sans copier le contenu dans Git. Il doit
mesurer les tailles réelles des deux lots Judge, vérifier que les vingt corps et
leurs EvidenceId survivent jusqu'au solveur, et produire un manifeste public
sans contenu. Ensuite seulement, le protocole d'un pilote live staged pourra
être préenregistré. Le reliquat de 0,57468470 USD reste inférieur aux deux
derniers parcours complets et ne doit pas être dépensé dans un pilote tronqué.
