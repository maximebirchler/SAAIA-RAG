# Contre-revue indépendante de la campagne complète v2

Relecture des captures déjà enregistrées, sans appel réseau, service, build, Git ni nouvelle exécution du worker. Les calculs Python relisent les bundles et comparent des identités, sommes et structures en mémoire ; aucun texte documentaire privé n'est reproduit ici.

## Résultat confirmé et périmètre exact

La campagne réelle couvre **12 documents synthétiques, 16 pages physiques, 90 faits annotés, 37 chunks SQL, 37 points Qdrant et 37 ancrages de publication**. Les douze jobs sont `done`, à leur première tentative ; le snapshot final contient zéro job actif. Le départ vide est enregistré séparément. Le runner déclare 12 enqueues API et aucune ligne canonique semée en SQL, aucun appel LLM payant.

La DLL identifiée dans les douze manifestes embarqués est `DD300C4EDB492825503C3F901B56D167543C30B267500E44D0240752968E8800`, identique au plan d'exécution. Le manifeste de corpus courant correspond exactement au hash enregistré `2AAFFFEDD4C2D736D0EE02D06482A0F76C9EB11C3160CFFAE062911F50AEA505`.

Contrôles relus ou recalculés :

- Les douze bundles ont les tailles gzip/décompressée et le hash attendus ; identités document/révision/source cohérentes. Les 37 checksums de chunk correspondent aux octets UTF-8 stockés.
- Les douze évaluations canoniques recalculées satisfont leurs attentes et les 90 faits physiques. Les seize lectures de contexte possèdent les identités document/révision/hash attendues et leurs faits exacts. La validation d'assemblage/chunking relève du runner et des manifestes de l'exécution réelle, pas d'une nouvelle auto-attestation du replay.
- Recalcul indépendant des 37 UUID Qdrant à partir de `qdrant|<chunk canonique sans tirets>|<plan complet>`, SHA-256 et ordre d'octets Guid .NET : aucun écart. Les payloads capturés exposent uniquement `chunk_id` et `ingestion_plan_fingerprint` ; cette capture ne prétend pas contenir les autres champs du payload.
- Les douze recherches positives utilisent un fait indépendant et le document/page prévus. Les 24 contrôles négatifs et les douze lectures du chunk trouvé sont cohérents. Vérification de 58 identités de hits retournés contre SQL : aucun écart de texte, chunk, document, révision, hash ou portée physique. Aucune dégradation de retriever enregistrée sur ces requêtes.
- Une requête aléatoire absente peut retourner des voisins denses actuels : le contrôle exige zéro correspondance exacte/sparse et des voisins denses dont l'identité est correcte. Il ne transforme pas la présence de ces voisins en faux échec, ni en correspondance lexicale réelle. Les contrôles de document absent restent vides.
- Cinq pages portent le désaccord d'ordre et l'association de section incertaine. Tous leurs blocs primaires conservés portent bien `sectionAssociation=not_asserted`. La réconciliation native a audité 13/16 pages et en a écarté trois peu exploitables nativement ; ce ne sont pas trois pages OCR manquantes.

`sqlPublicationValidated`, `vectorStorePublicationValidated` et `canonicalSearchValidated` sont ainsi justifiés pour ce corpus et ce profil. **`vectorValidated=false` demeure correct** : cette campagne ne mesure pas un rappel sémantique général ni toutes les recherches possibles.

## Les deux avertissements de grille ne masquent pas une cellule utile manquante

| Document / page | Grille observée | Cellules et couverture | Seule case non matérialisée |
|---|---|---|---|
| `fr-table-critical-v2`, page 1 | 4×3 | 9 objets, en-tête fusionné sur 3 colonnes ; 11 positions couvertes, zéro chevauchement | ligne 3 / colonne 2, explicitement vide dans l'annotation indépendante |
| `structured-table-v1`, page 1 | 5×4 | 16 objets, en-tête fusionné sur 4 colonnes ; 19 positions couvertes, zéro chevauchement | ligne 3 / colonne 3, explicitement vide dans l'annotation indépendante |

Indices à partir de zéro. Le calcul du produit tient déjà compte des spans : `canonical_table_sparse_grid` signale ici ces emplacements sans objet cellule, conformément à sa définition. Il ne signale pas un trou de valeur supplémentaire. Les deux cellules volontairement vides ne sont pas remplies avec des valeurs voisines. Le contrôle des tableaux et de leurs cellules reste indépendant de la seule présence des 90 faits. La même conclusion a fait l'objet d'une contre-relecture séparée de l'agent atomicité.

## Les quatre avertissements de figures

| Document / page | Image inventoriée | Lignes de texte de l'image présentes exactement dans canonique et retrieval |
|---|---|---|
| `fr-mixed-critical-v2`, page 2 | page raster | 5/5 |
| `fr-scan-critical-v2`, page 1 | page raster | 6/6 |
| `full-page-scan-v1`, page 1 | page raster | 6/6 |
| `mixed-image-text-v1`, page 1 | panneau raster | 3/3 |

Ces 20 lignes ont été comparées aux listes littérales UTF-8 du générateur figé, au-delà des seuls marqueurs critiques. Les accents, signes et ponctuations n'ont pas été supprimés pour obtenir la concordance. Ces quatre images synthétiques contiennent du texte imprimé et des cadres décoratifs ; aucun diagramme quantitatif, schéma technique ou contenu photographique n'y est à interpréter. `figure_semantics_not_extracted` signifie donc ici que l'interprétation visuelle générale n'a pas été effectuée, **sans perte de texte OCR établie**. Cela ne qualifie pas l'analyse sémantique de figures arbitraires.

## Résidus et limites techniques à conserver

1. **Vecteurs anciens et GC.** Les points sont isolés par plan et les recherches filtrent le plan SQL actif. Aucun GC automatique des anciens plans n'est qualifié : des points obsolètes peuvent consommer espace et fenêtres de classement. L'overfetch reste borné à trois recherches et signale une fenêtre incomplète. La collection neuve de cette campagne ne constitue pas une preuve de nettoyage des anciennes générations. Le cycle de vie réel, désactivé dans ce run, doit être prouvé séparément.
2. **Profil effectivement exécuté.** Docling Serve 1.38.1 / Slim 2.142.1 / Parse 7.23.0, modèles IBM 4.0.4, RapidOCR 3.9.1, ONNX Runtime 1.23.2, Torch 2.14.1 sont observés. Ce déploiement local Windows CPU n'est pas une exécution de l'image Docker épinglée. On ne transfère pas automatiquement son acceptation à cette image ou à d'autres versions.
3. **Embeddings.** Le modèle réel est multilingual-e5-base, révision `d128750597153bb5987e10b1c3493a34e5a4502a`, dimension 768, adaptateur Transformers CPU. Les manifestes emploient encore l'intitulé technique `text-embeddings-inference` pour le stade de protocole ; son runtime explicite identifie bien l'adaptateur CPU. Cela n'atteste pas le binaire TEI. Les compteurs de `/info` sont ceux du service partagé sur sa durée de vie ; ils ne sont pas attribués intégralement à cette seule campagne.
4. **Autres moteurs et couverture.** OCRmyPDF/Tesseract externe, manuscrit, scans très dégradés, toutes les langues et toutes les structures documentaires ne sont pas qualifiés ici. Les réparations régionales de tableaux montrent deux tentatives terminées, zéro réparation appliquée et zéro échec : ce run ne prouve pas le chemin d'une réparation effectivement sélectionnée.
5. **Sémantique et produit.** L'absence de faux titre démontrée sur les pages en conflit ne fournit pas une nouvelle hiérarchie sémantique. Aucun planning de repas ni réponse LLM payante n'est approuvé par ces preuves d'ingestion. Le statut global reste `TESTE_NON_APPROUVE`.

## Précontrôle du protocole de cycle de vie

Le helper reste limité au PDF synthétique possédé, avec source/hash figés, vérification de propriété avant les transports, mutations d'état par API et SQL en lecture seule. Seule la copie de travail de ce PDF est supprimée/recréée ; la source figée est distincte. Les étapes contrôlent l'upsert inchangé, la suppression, la recréation et, si l'état running est observé, une annulation. Les recherches restent bornées aux hits demandés : elles ne prouvent pas l'absence physique de tous les anciens points Qdrant.

Un défaut de preuve a été reproduit : le protocole acceptait un terminal `canceled` comme annulation validée même quand l'API répondait `canceled=false`. Capture pure mock `lifecycle-negative-ack-red-v1` conservée. Le helper exige maintenant un booléen `true` explicite avant de revendiquer l'annulation ; sinon l'ensemble échoue et l'annulation est `NOT_CONFIRMED`.

Après correction : `lifecycle-negative-ack-green-v1` passe ; le protocole nominal `lifecycle-offline-protocol-v2` et les 16 gardes `lifecycle-offline-guards-v2` passent. Ces trois preuves utilisent exclusivement des transports simulés. Elles ne remplacent pas le futur cycle réel. `upstreamEngineCancellationVerified` reste toujours faux : une annulation API ou un job terminal ne prouve pas que le calcul Docling amont a été interrompu.
