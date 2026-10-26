# Contrôle de recherche hybride : cause et correction du harnais

Les douze jobs de `worker-full-e5-071-v1` étaient terminés. Le vérificateur
refusait pourtant les douze recherches parce qu'il exigeait une liste vide
pour une requête contenant un nonce absent du document.

Cette attente contredisait le contrat réel de `CanonicalRetrieval` : le chemin
source-backed exécute exact, sparse et dense, puis fusionne leurs résultats.
Le score minimal par défaut vaut zéro. Des voisins denses pour un nonce ne
constituent donc pas une correspondance littérale prétendue par le moteur.

Les captures figées montrent, dans les douze cas : zéro résultat exact, zéro
résultat sparse, seulement des voisins `dense_qdrant` du document courant pour
le nonce. Les douze requêtes portant sur un UUID de document inexistant ont
bien renvoyé zéro résultat total. Onze recherches positives retrouvaient le
fait annoté et permettaient une lecture exacte du chunk.

## Correction bornée

Le harnais exige maintenant pour le nonce : zéro correspondance exacte/sparse,
compteurs de canaux cohérents, et identité, révision, source, page, chunk et
texte SQL exacts pour chaque éventuel voisin dense. Un voisin d'un autre
document, d'une ancienne révision ou au texte modifié reste refusé. Le contrôle
de document inexistant exige toujours une liste vide. La dégradation des
retrievers reste un échec.

Le résumé distingue `absentQueryLexicalMatchesZero`,
`absentQueryDenseNeighborsCurrent`, le nombre de voisins denses et
`absentDocumentEmpty`. Il ne présente plus ce contrôle comme deux listes
nécessairement vides. Aucun seuil produit, moteur, requête ou donnée canonique
n'a été modifié.

## Validation exécutée sans moteur

- RED : le nouveau témoin de voisins denses courants a échoué sur le
  vérificateur précédent, dans `tests/hybrid-nonce-harness-red-v1`.
- GREEN : suite Python complète, 50 tests réussis, sources inchangées pendant
  l'exécution, dans `tests/hybrid-nonce-harness-green-v1`. Les contre-contrôles
  exigent encore le rejet des correspondances lexicales, des identités fausses,
  des textes modifiés et du résultat non vide sur le document inexistant.
- Réévaluation des mêmes captures uniquement : nouveau fichier
  `worker-full-e5-071-v1/publication-verification-v2.json`, avec liaison des
  hashes d'entrée avant/après dans `offline-reevaluation-v2.json`. Aucun ancien
  fichier de preuve n'est écrasé.

La recherche passe désormais onze cas sur douze. Le tableau français reste en
échec parce que le fait positif n'est pas retrouvé fidèlement ; ce défaut n'a
pas été masqué. L'acceptation globale reste à cinq cas sur douze : sept cas
conservent des écarts de structure, géométrie, ordre ou fidélité à diagnostiquer
séparément. La publication Qdrant garde ses douze contrôles d'identité/plan
réussis ; cela ne suffit pas à approuver la fidélité documentaire.

Appels HTTP, opérations SQL, services lancés, credentials lus et appels modèles
pour cette correction/réévaluation : zéro. Les jeux Python sont synthétiques ;
la réévaluation lit uniquement les observations privées déjà capturées. Le
produit demeure `TESTE_NON_APPROUVE`.
