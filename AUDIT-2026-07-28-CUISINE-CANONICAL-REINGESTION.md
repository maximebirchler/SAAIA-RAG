# Audit — Réingestion canonique Cuisine

> Date de contrôle : 2026-07-28
> Serveur : `saaia-server`
> Backend déployé après contrôle : build distant
> `/opt/saaia/deploy/backend-build-20260728-212836`
> État : corpus canonique validé; migration de six anciennes cartes de chat à
> traiter avant purge définitive des tombstones SQL.

## Conclusion

Les dix PDF attendus sous `Cuisine/<fichier>` ont tous été réingérés et publiés
avec le profil `deterministic_canonical_v3`. Pour chaque document :

- le document est `indexed`;
- la révision la plus récente est publiée le 2026-07-28;
- `ingestion_version == indexed_version`;
- un profil canonique unique existe;
- des ancres, chunks et cartes canoniques existent;
- le nombre de points Qdrant du `doc_id + ingestion_version` actif est
  strictement égal au nombre de `retrieval_chunks`;
- le nombre total de points du `doc_id` est identique, donc aucune ancienne
  version vectorielle du même document ne subsiste.

Les dix anciennes lignes `Cuisine/PDF/<fichier>` sont `deleted` et leurs
anciens `doc_id` possèdent zéro point Qdrant. Les dix fichiers physiques ont
été comparés à leur équivalent canonique par taille et SHA-256, puis supprimés
individuellement. Le dossier vide `Cuisine/PDF` a été retiré. Dix fichiers
canoniques demeurent.

## Inventaire canonique

| Document | Version | Ancres | Chunks SQL | Points Qdrant | Cartes |
|---|---:|---:|---:|---:|---:|
| `14911887_9001116052_NFFS4I_fr_fm.pdf` | 73 | 123 | 123 | 123 | 134 |
| `30-recettes-preferees-des-francais.pdf` | 120 | 137 | 137 | 137 | 61 |
| `9782317030376.pdf` | 71 | 37 | 37 | 37 | 30 |
| `chefbot_livre_de_recettes_fr.pdf` | 60 | 512 | 512 | 512 | 98 |
| `facilitemps.pdf` | 68 | 378 | 378 | 378 | 185 |
| `Je_cuisine_simplement.pdf` | 72 | 279 | 279 | 279 | 89 |
| `livre-recette-sist-2025-web.pdf` | 74 | 176 | 176 | 176 | 114 |
| `nobilia-recettes-internationales-FR.pdf` | 74 | 453 | 453 | 453 | 125 |
| `si-on-cuisinait.pdf` | 72 | 537 | 537 | 537 | 119 |
| `Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf` | 90 | 1 250 | 1 250 | 1 250 | 168 |
| **Total** | — | **3 882** | **3 882** | **3 882** | **1 123** |

## Preuve des doublons physiques

Avant suppression :

- 10 fichiers dans `/opt/saaia/documents/Cuisine`;
- 10 fichiers dans `/opt/saaia/documents/Cuisine/PDF`;
- même nom, même taille et même SHA-256 pour chacune des dix paires.

Après suppression :

- 10 fichiers dans `/opt/saaia/documents/Cuisine`;
- 0 fichier dupliqué;
- le dossier `/opt/saaia/documents/Cuisine/PDF` n’existe plus.

Le script de suppression a vérifié le `realpath` du répertoire, l’existence des
deux fichiers et l’égalité SHA-256 avant chaque `rm`. Aucune suppression
récursive n’a été utilisée.

## Déduplication du scanner

La route de scan administratif utilise maintenant le même
`IngestionDuplicateFilePlanner` que le scanner automatique. La correction a
été validée avant déploiement :

- 26/26 tests backend ciblés verts;
- tests explicites du choix du chemin le moins profond;
- tests du cache de hash;
- tests du scanner avec et sans suppression demandée;
- tests du nouveau retrieval canonique inclus dans la même validation.

Le backend a ensuite été reconstruit sur le serveur, redémarré et validé par
`/ready`. Post-déploiement :

- Postgres : prêt;
- Qdrant avec authentification : prêt;
- TEI : prêt;
- Docling : prêt;
- OCR : prêt;
- provenance canonique : `ready`;
- aucun job Cuisine `queued`, `running` ou `paused`.

Artefact de test :

`artifacts/test-results/cuisine-dedup-predeploy-20260728/cuisine-dedup-predeploy.trx`

Artefacts de déploiement :

`artifacts/deploy/backend-canonical-20260728-2125/`

## Tombstones SQL et historique de chat

Les dix documents `Cuisine/PDF` restent pour l’instant comme tombstones
`deleted`. Ils référencent encore :

- 20 révisions historiques;
- 20 profils;
- 2 584 anciennes cartes;
- 45 processing runs;
- 75 jobs d’ingestion historiques.

Ils ne sont pas actifs, ne sont pas recherchables et ne possèdent aucun point
Qdrant. Ils ne sont donc ni des doublons actifs ni des orphelins d’index.

Six messages de chat persistés contiennent encore un ancien chemin
`Cuisine/PDF/...` dans leur `sources_json`. Supprimer immédiatement les
tombstones et leurs artefacts rendrait plus difficile la migration de ces
anciennes cartes vers le chemin canonique. La prochaine étape propre est une
migration générique fondée sur le hash de source :

1. résoudre chaque ancien `docPath/docId/sourceHash` vers l’unique document
   actif de même hash;
2. mettre à jour le payload de source avec le chemin et le `doc_id` canoniques;
3. vérifier que les six cartes ouvrent la source canonique;
4. seulement ensuite purger transactionnellement les dix documents supprimés,
   leurs artefacts en cascade et les jobs historiques devenus inutiles.

Cette migration doit être générique pour toute catégorie et tout document.
Aucun nom Cuisine ne doit entrer dans le code produit.
